using Microsoft.AspNetCore.Identity;
using Sw5e.Identity;
using Sw5e.Identity.Administration;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Writes the record of what an administrator did.
/// </summary>
/// <remarks>
/// <para>
/// One method, called by every administrative handler, for the same reason
/// <c>AccountSessions</c> is one method called by every sign-in route: the
/// property that matters is one they must all share, and a handler that forgot
/// would produce an action nobody can account for — a failure that looks like
/// nothing at all until somebody asks who granted a role.
/// </para>
/// <para>
/// <b>It stages the row rather than saving it.</b> Nothing here calls
/// <c>SaveChangesAsync</c>. The identity <c>DbContext</c> is scoped, and it is
/// the same instance the <c>UserManager</c> writes through, so an entry added
/// here is flushed by whatever write the handler performs next — one
/// <c>SaveChanges</c>, one implicit transaction, and therefore no state in
/// which a role was granted and the record of it was not, or the reverse. That
/// matters most for deletion, where the record has to be written in the same
/// breath as the row it describes disappearing.
/// </para>
/// <para>
/// The display names are copied onto the row at this moment. See
/// <see cref="AdministrativeAction"/> for why an audit entry cannot resolve
/// them at read time the way the flag queue does.
/// </para>
/// </remarks>
internal static class AdministrativeLog
{
    /// <summary>
    /// Stages one administrative action against the identity context.
    /// </summary>
    /// <param name="rolesBefore">
    /// The subject's assignable roles beforehand, or null for an action that
    /// was not about roles. Only assignable roles are recorded: Community is
    /// the floor every account stands on and can never be the thing that
    /// changed, so listing it would add a constant to every row.
    /// </param>
    public static void Record(
        Sw5eIdentityDbContext store,
        Sw5eUser actor,
        Sw5eUser subject,
        AdministrativeActionKind kind,
        TimeProvider clock,
        string? reason = null,
        IEnumerable<string>? rolesBefore = null,
        IEnumerable<string>? rolesAfter = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(clock);

        store.AdministrativeActions.Add(new AdministrativeAction
        {
            // Version 7, so the primary key is already in creation order and
            // the newest-first listing rides the index it already has.
            Id = Guid.CreateVersion7(),
            Action = AdministrativeActionWire.NameOf(kind),
            ActorUserId = actor.Id,
            ActorDisplayName = actor.DisplayName,
            SubjectUserId = subject.Id,
            SubjectDisplayName = subject.DisplayName,
            RolesBefore = Join(rolesBefore),
            RolesAfter = Join(rolesAfter),
            Reason = reason,
            CreatedAt = clock.GetUtcNow(),
        });
    }

    /// <summary>
    /// Renders a role set for storage: assignable roles only, in the ladder's
    /// own order, comma separated, and null when there were none.
    /// </summary>
    /// <remarks>
    /// Ordered by <c>Sw5eRoles.Assignable</c> rather than alphabetically so
    /// that two rows describing the same set are byte-identical, which is what
    /// makes "did this change anything" answerable by comparing the two
    /// columns.
    /// <para>
    /// Null therefore covers two cases — the action was not about roles, and
    /// the account held no assignable role — and that is deliberate rather than
    /// sloppy. The column is not what says which happened; <c>Action</c> is,
    /// and a reader who needs to tell "revoked everything" from "suspended"
    /// looks there. Inventing a sentinel string for the empty set would put a
    /// value in the column that is not a role name, which is how a later query
    /// filtering on role names quietly stops matching.
    /// </para>
    /// </remarks>
    private static string? Join(IEnumerable<string>? roles)
    {
        if (roles is null)
        {
            return null;
        }

        var held = Sw5eRoles.Assignable
            .Where(role => roles.Contains(role, StringComparer.Ordinal))
            .ToArray();

        return held.Length == 0 ? null : string.Join(",", held);
    }

    /// <summary>Reads a stored role set back into a list, for the wire.</summary>
    public static IReadOnlyList<string>? Split(string? stored) =>
        string.IsNullOrEmpty(stored)
            ? null
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
