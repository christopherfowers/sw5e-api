using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;
using Sw5e.Identity.Administration;
using Sw5e.Identity.Email;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// The administrative surface: granting and revoking the roles that decide who
/// may upload content.
/// </summary>
/// <remarks>
/// This is the most dangerous endpoint in the API — it is the one that creates
/// more administrators — so it is also the smallest. It does one thing, it
/// takes a declaration of the end state rather than an instruction to add,
/// and every use of it is logged at warning level and emailed to the account it
/// affected.
/// </remarks>
internal static class AdministrationHandlers
{
    public static async Task<Results<Ok<AccountRolesResponse>, ProblemHttpResult>> AssignRolesAsync(
        Guid userId,
        AssignRolesRequest request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        Sw5eIdentityDbContext store,
        IAccountEmailSender email,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Sw5e.Api.Accounts");

        if (!TryReadRoles(request.Roles, out var requested, out var problem))
        {
            return problem!;
        }

        var target = await users.FindByIdAsync(userId.ToString());

        if (target is null)
        {
            return AccountProblems.NoSuchAccount;
        }

        var actor = await users.GetUserAsync(context.User);

        if (actor is null)
        {
            return AccountProblems.NotAuthenticated;
        }

        // An administrator cannot remove their own administrator role. Not
        // paternalism: the role is the only thing that can grant the role, so
        // the last administrator revoking themselves leaves the platform with
        // no way to appoint another short of editing the database by hand. It
        // also removes the most attractive move available to somebody who has
        // just stolen an administrator's session — locking the real
        // administrators out of their own platform.
        if (actor.Id == target.Id && !requested.Contains(Sw5eRoles.Administrator))
        {
            return AccountProblems.Invalid(
                "An administrator cannot remove their own administrator role. Ask another " +
                "administrator to do it.");
        }

        var held = await users.GetRolesAsync(target);

        var toGrant = requested.Except(held, StringComparer.Ordinal).ToArray();
        var toRevoke = Sw5eRoles.Assignable
            .Where(role => held.Contains(role, StringComparer.Ordinal) && !requested.Contains(role))
            .ToArray();

        if (toGrant.Length > 0 && !(await users.AddToRolesAsync(target, toGrant)).Succeeded)
        {
            return AccountProblems.Invalid("Those roles could not be granted.");
        }

        if (toRevoke.Length > 0 && !(await users.RemoveFromRolesAsync(target, toRevoke)).Succeeded)
        {
            return AccountProblems.Invalid("Those roles could not be revoked.");
        }

        var elevated = requested.Contains(Sw5eRoles.Contributor) ||
                       requested.Contains(Sw5eRoles.Administrator);

        // A passkey or an authenticator app. Either satisfies the requirement,
        // because either proves possession of something during sign-in; a
        // mailbox does not, which is why an emailed code is not on this list.
        var hasSecondFactor =
            target.TwoFactorEnabled || (await users.GetPasskeysAsync(target)).Count > 0;

        var awaitingSecondFactor = elevated && !hasSecondFactor;

        if (toGrant.Length > 0 || toRevoke.Length > 0)
        {
            // The record of the change, in a table the administrator who made
            // it cannot afterwards edit — the migration puts an append-only
            // trigger over it. Staged here and flushed by the security stamp
            // rotation below, which writes through the same scoped context, so
            // the grant and the record of it are one transaction.
            //
            // The log line further down stays. It is what an operator watching
            // a stream sees in the moment; this is what somebody asks six
            // months later, and a log stream is not a place to ask a question.
            AdministrativeLog.Record(
                store,
                actor,
                target,
                AdministrativeActionKind.RolesChanged,
                clock,
                rolesBefore: held,
                rolesAfter: requested);

            // Rotating the stamp is what makes a revocation take effect on a
            // session that is already open. Without it the demoted account
            // keeps its old role claims until its cookie expires — up to eight
            // hours of privilege somebody has just decided it should not have.
            // With it, the security stamp validator drops the session at its
            // next check, within five minutes.
            await users.UpdateSecurityStampAsync(target);

            logger.LogWarning(
                "Account {ActorId} set the roles on account {TargetId}: granted [{Granted}], revoked [{Revoked}].",
                actor.Id,
                target.Id,
                string.Join(", ", toGrant),
                string.Join(", ", toRevoke));

            // The account finds out from the platform rather than by noticing.
            // A silent privilege change is one nobody can dispute.
            //
            // When the new role cannot be used yet, the message says so and
            // says what to do about it. The alternative — the ordinary notice —
            // would leave somebody who has just been made a contributor to
            // discover on their own that the contributor tools answer 403, and
            // the most likely conclusion they would draw is that the grant did
            // not happen.
            await email.SendSecurityNoticeAsync(
                new AccountEmailRecipient(target.Email!, target.DisplayName),
                awaitingSecondFactor
                    ? "The permissions on your account were changed by an administrator. " +
                      "Before you can use them you need to add a passkey or an authenticator " +
                      "app: roles that let you publish content require one, and your account " +
                      "does not have either yet. Both are set up from your account settings."
                    : "The permissions on your account were changed by an administrator.",
                cancellationToken);
        }

        if (awaitingSecondFactor)
        {
            logger.LogWarning(
                "Account {TargetId} holds an elevated role with no passkey and no authenticator, " +
                "so it cannot use it until one is enrolled.",
                target.Id);
        }

        var roles = await users.GetRolesAsync(target);

        return TypedResults.Ok(new AccountRolesResponse(
            target.Id,
            [.. roles.OrderBy(role => role, StringComparer.Ordinal)],
            awaitingSecondFactor));
    }

    private static bool TryReadRoles(
        IReadOnlyList<string>? requested,
        out HashSet<string> roles,
        out ProblemHttpResult? problem)
    {
        roles = new HashSet<string>(StringComparer.Ordinal);
        problem = null;

        if (requested is null)
        {
            problem = AccountProblems.Invalid("A 'roles' array is required. Send an empty array to revoke everything.");
            return false;
        }

        foreach (var role in requested)
        {
            // Matched against the closed list, exactly, with an ordinal
            // comparison. Case-insensitive matching would be friendlier and
            // would also mean the set of things that count as "Administrator"
            // is decided by a culture-sensitive comparison rather than by this
            // file.
            var match = Sw5eRoles.Assignable.FirstOrDefault(
                assignable => string.Equals(assignable, role, StringComparison.Ordinal));

            if (match is null)
            {
                problem = AccountProblems.Invalid(
                    $"'{role}' is not an assignable role. Assignable roles are: " +
                    string.Join(", ", Sw5eRoles.Assignable) + ".");
                return false;
            }

            roles.Add(match);
        }

        return true;
    }
}
