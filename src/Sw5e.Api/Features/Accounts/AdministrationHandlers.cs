using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;
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
        IAccountEmailSender email,
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

        if (toGrant.Length > 0 || toRevoke.Length > 0)
        {
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
            await email.SendSecurityNoticeAsync(
                new AccountEmailRecipient(target.Email!, target.DisplayName),
                "The permissions on your account were changed by an administrator.",
                cancellationToken);
        }

        var roles = await users.GetRolesAsync(target);

        return TypedResults.Ok(new AccountRolesResponse(
            target.Id,
            [.. roles.OrderBy(role => role, StringComparer.Ordinal)]));
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
