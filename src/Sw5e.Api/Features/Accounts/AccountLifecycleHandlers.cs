using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Sw5e.Domain.Content;
using Sw5e.Identity;
using Sw5e.Identity.Administration;
using Sw5e.Identity.Email;
using Sw5e.Identity.EmailSignIn;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Suspending an account, reinstating it, and deleting it.
/// </summary>
/// <remarks>
/// <para>
/// Three actions and two very different kinds of thing. Suspension is a
/// decision that can be taken back; deletion is not, and the shape of this file
/// is mostly about keeping the second from happening by accident on the way to
/// the first.
/// </para>
/// <para>
/// <b>Neither may be aimed at the caller's own account.</b> The platform
/// already refused self-demotion, on the grounds that the administrator role is
/// the only thing that can grant the administrator role. Suspension and
/// deletion reach the same end state by other doors, so both are closed the
/// same way — and with all three closed, the number of administrators cannot
/// reach zero through this API at all, which is a property rather than a
/// warning in a document.
/// </para>
/// </remarks>
internal static class AccountLifecycleHandlers
{
    /* ----------------------------------------------------------- suspension */

    public static async Task<Results<Ok<AccountSuspensionStateResponse>, ProblemHttpResult>> SetSuspensionAsync(
        Guid userId,
        SetSuspensionRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        Sw5eIdentityDbContext store,
        EmailSignInCodeService codes,
        IAccountEmailSender email,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategories.Accounts);

        if (request?.Suspended is not { } suspended)
        {
            return AccountProblems.Invalid(
                "A 'suspended' flag is required. Send true to suspend the account and false to " +
                "reinstate it.");
        }

        var reason = request.Reason?.Trim();

        if (suspended)
        {
            // Required, because a suspension with no stated reason cannot be
            // reviewed by the next administrator and cannot be defended to the
            // person it was applied to. This is the one field in the whole
            // administrative surface that is compulsory, and it is compulsory
            // for that reason alone.
            if (string.IsNullOrEmpty(reason))
            {
                return AccountProblems.Invalid(
                    "A reason is required when suspending an account. It is written for the " +
                    "other administrators and is never shown to the account.");
            }

            if (reason.Length > AccountSuspension.MaxReasonLength)
            {
                return AccountProblems.Invalid(
                    $"A reason is at most {AccountSuspension.MaxReasonLength} characters.");
            }
        }
        else if (!string.IsNullOrEmpty(reason))
        {
            // Refused rather than ignored. There is nowhere to store a reason
            // for a reinstatement, and accepting one would mean an
            // administrator writing an explanation that goes nowhere and
            // believing it was recorded.
            return AccountProblems.Invalid(
                "A reason cannot be given when reinstating an account. Nothing stores it, and " +
                "the administrative log records who lifted the suspension and when.");
        }

        if (await users.FindByIdAsync(userId.ToString()) is not { } target)
        {
            return AccountProblems.NoSuchAccount;
        }

        if (await users.GetUserAsync(context.User) is not { } actor)
        {
            return AccountProblems.NotAuthenticated;
        }

        if (actor.Id == target.Id)
        {
            return AccountProblems.NotOnYourself("suspend or reinstate");
        }

        var alreadySuspended = AccountSuspension.IsSuspended(target);

        if (alreadySuspended == suspended)
        {
            // Restating the current state is refused rather than answered 200,
            // for the reason the flag queue refuses a no-op transition: it is
            // almost always two administrators acting on the same account, and
            // telling the second one they did something they did not do is how
            // a suspension gets attributed to the wrong person.
            return AccountProblems.Invalid(
                suspended
                    ? "That account is already suspended."
                    : "That account is not suspended.");
        }

        var now = clock.GetUtcNow();

        target.SuspendedAt = suspended ? now : null;
        target.SuspensionReason = suspended ? reason : null;
        target.SuspendedByUserId = suspended ? actor.Id : null;

        AdministrativeLog.Record(
            store,
            actor,
            target,
            suspended
                ? AdministrativeActionKind.AccountSuspended
                : AdministrativeActionKind.AccountReinstated,
            clock,
            reason: suspended ? reason : null);

        // Writes the account and the log entry together: the UserManager saves
        // through this same scoped context, so the staged audit row goes out in
        // the same SaveChanges. See AdministrativeLog.
        if (!(await users.UpdateAsync(target)).Succeeded)
        {
            return AccountProblems.Invalid("That account could not be updated.");
        }

        // Rotates the security stamp, which invalidates every outstanding
        // emailed token and — on its own — would drop the account's live
        // sessions at the stamp validator's next interval. Suspension does not
        // rely on that: AccountSuspension ends the session on the very next
        // request. The rotation is here so that reinstatement is equally
        // thorough, because a link minted before a suspension must not still
        // work after it.
        await users.UpdateSecurityStampAsync(target);

        // Any live sign-in code sitting in the mailbox is a working credential
        // for an account that is not allowed to sign in. The confirmation check
        // would refuse it anyway; discarding it means there is not a spare key
        // lying around for the ten minutes after the decision.
        if (users.NormalizeEmail(target.Email) is { } normalized)
        {
            await codes.DiscardAsync(normalized, cancellationToken);
        }

        logger.LogWarning(
            "Account {ActorId} {Action} account {TargetId}.",
            actor.Id,
            suspended ? "suspended" : "reinstated",
            target.Id);

        // The account is told, and told nothing more than that. The reason is
        // written for other administrators; quoting it back would, where the
        // reason is an investigation, tell the subject what is being
        // investigated. What they are given instead is the one thing they can
        // act on: who to write to.
        await email.SendSecurityNoticeAsync(
            new AccountEmailRecipient(target.Email!, target.DisplayName),
            suspended
                ? "An administrator has suspended your account. You will not be able to sign in " +
                  "while the suspension stands, and any session you had open has ended. Your " +
                  "passkeys and your contributions are untouched. Reply to this message if you " +
                  "believe this is a mistake."
                : "An administrator has lifted the suspension on your account. You can sign in " +
                  "again with the passkey or authenticator you already had.",
            cancellationToken);

        return TypedResults.Ok(new AccountSuspensionStateResponse(
            target.Id,
            UserDirectoryHandlers.Describe(target)));
    }

    /* ------------------------------------------------------------- deletion */

    /// <summary>
    /// Deletes an account, and does not delete what it wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What goes.</b> The identity row and everything the framework cascades
    /// from it: the address, the display name, the role memberships, the
    /// passkeys, the authenticator secret and its recovery codes. Any emailed
    /// sign-in codes are discarded explicitly, because that table deliberately
    /// carries no foreign key to the account and so cannot cascade. After this
    /// the person is not on the platform: nothing can be signed in to, nothing
    /// can be recovered, and the address is free for somebody else to register.
    /// </para>
    /// <para>
    /// <b>What stays, and why.</b> Content revisions keep their
    /// <c>actor_user_id</c> and moderation reports keep their
    /// <c>reporter_user_id</c>. Neither is rewritten, reassigned or blanked.
    /// This is the decision worth defending, so: a revision is the record of a
    /// change to canonical rules that a whole community reads, and the
    /// revision table is append-only at the database precisely so that the
    /// people who can make those changes cannot quietly unmake the record of
    /// having made them. An account deletion that reached in and erased
    /// authorship would be exactly that, with a friendlier name — and it would
    /// be available to any administrator, against any contributor, at any time.
    /// A history that can be edited by deleting an account is not a history.
    /// </para>
    /// <para>
    /// What the reader sees instead is "a removed account", which the flag
    /// queue's contract already documents and already renders: an identifier
    /// with no account behind it is a real state rather than an error, and it
    /// is the honest rendering of "somebody wrote this and is no longer here".
    /// Authorship does not vanish; the person does.
    /// </para>
    /// <para>
    /// <b>Drafts are neither.</b> A draft is not history — it is unfinished
    /// work holding the only editing slot for the document it names — so it is
    /// the one thing that will refuse a deletion outright. See
    /// <c>AccountProblems.DraftsOutstanding</c>.
    /// </para>
    /// </remarks>
    public static async Task<Results<Ok<AccountDeletedResponse>, ProblemHttpResult>> DeleteAsync(
        Guid userId,
        // In the body rather than in a query string, even though a body on a
        // DELETE is the less common shape. A reason names a person and says
        // something about their conduct, and a query string is written to every
        // access log and proxy log between the browser and the process.
        //
        // Both halves of the attribute are load-bearing. Minimal APIs refuse to
        // *infer* a body on DELETE — the endpoint fails to map at startup, which
        // takes the whole application down rather than one route — so the source
        // has to be stated. And EmptyBodyBehavior.Allow is what keeps a caller
        // who has no reason to give from having to send `{}`; without it the
        // parameter is required and a bodiless DELETE is a 400.
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteAccountRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        Sw5eIdentityDbContext store,
        EmailSignInCodeService codes,
        [FromServices] IContentAuthoringStore? authoring,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LogCategories.Accounts);

        if (await users.FindByIdAsync(userId.ToString()) is not { } target)
        {
            return AccountProblems.NoSuchAccount;
        }

        if (await users.GetUserAsync(context.User) is not { } actor)
        {
            return AccountProblems.NotAuthenticated;
        }

        if (actor.Id == target.Id)
        {
            return AccountProblems.NotOnYourself("delete");
        }

        var note = request?.Reason?.Trim();

        if (note is { Length: > AccountSuspension.MaxReasonLength })
        {
            return AccountProblems.Invalid(
                $"A reason is at most {AccountSuspension.MaxReasonLength} characters.");
        }

        if (authoring is not null)
        {
            var drafts = (await authoring.ListDraftsAsync(cancellationToken))
                .Count(draft => draft.CreatedByUserId == target.Id);

            if (drafts > 0)
            {
                return AccountProblems.DraftsOutstanding(drafts);
            }
        }

        // Discarded before the account goes, because the lookup is by
        // normalised address and the address is about to stop existing.
        if (users.NormalizeEmail(target.Email) is { } normalized)
        {
            await codes.DiscardAsync(normalized, cancellationToken);
        }

        // Staged before the delete so that both are in one SaveChanges, and so
        // the display name is copied while there is still a row to copy it
        // from. The order is not cosmetic: an audit row written afterwards
        // could fail and leave a deletion nobody recorded, and one written and
        // committed beforehand could describe a deletion that then failed.
        AdministrativeLog.Record(
            store,
            actor,
            target,
            AdministrativeActionKind.AccountDeleted,
            clock,
            reason: string.IsNullOrEmpty(note) ? null : note,
            rolesBefore: await users.GetRolesAsync(target));

        if (!(await users.DeleteAsync(target)).Succeeded)
        {
            return AccountProblems.Invalid("That account could not be deleted.");
        }

        logger.LogWarning(
            "Account {ActorId} deleted account {TargetId}. Its revisions and reports keep the " +
            "identifier and now render as a removed account.",
            actor.Id,
            target.Id);

        return TypedResults.Ok(AccountDeletedResponse.For(target.Id));
    }
}
