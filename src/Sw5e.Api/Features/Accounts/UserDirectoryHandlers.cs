using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sw5e.Domain.Content;
using Sw5e.Identity;
using Sw5e.Identity.Administration;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Finding a person, and reading what has been done to them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this had to exist before anything else here.</b> The platform already
/// had a role assignment endpoint, addressed by account identifier, and no way
/// on earth to obtain an account identifier: no listing, no search, no lookup
/// by address. The one administrative capability the API had could therefore
/// only be used by somebody with a database client open, which means it could
/// not be used at all by the people it was built for. Everything else in this
/// feature is downstream of being able to answer "which account is
/// jaina@example.test".
/// </para>
/// <para>
/// <b>What this is, stated plainly, because it decides every other rule.</b>
/// It is a directory of real people's email addresses. There is no version of
/// it for Contributors, no version for Community accounts, and no version that
/// answers a narrower question to an anonymous caller. The route requires
/// <c>Sw5ePolicies.Administer</c> — which is the Administrator role
/// <em>and</em> a session established with a passkey or an authenticator — and
/// that check runs in the authorization middleware, before this file is
/// reached. Nothing here can leak, because nothing here runs for anybody who
/// should not see it: a Community caller's 403 is written by the cookie
/// handler, costs no query, and is byte-for-byte the same whether the account
/// they asked about exists, does not exist, or is the administrator's own.
/// </para>
/// <para>
/// <b>Everything is bounded before it reaches the database.</b> The search term
/// has a minimum length and a maximum, the page size is clamped, and the
/// filters are parsed against closed lists rather than pasted into a predicate.
/// An administrator is trusted and an administrator's session can still be
/// stolen, and a search endpoint with no floor on the term is a full table dump
/// one empty string away.
/// </para>
/// </remarks>
internal static class UserDirectoryHandlers
{
    /// <summary>Largest page the directory will hand out.</summary>
    private const int MaxPageSize = 100;

    private const int DefaultPageSize = 25;

    /// <summary>
    /// Shortest search term that will be honoured.
    /// </summary>
    /// <remarks>
    /// Two characters. Below that a term matches most of the table, which makes
    /// "search" an expensive synonym for "list everything" and makes the
    /// resulting page useless to read. A caller who genuinely wants everything
    /// omits the term, which is cheaper for both ends.
    /// </remarks>
    private const int MinSearchLength = 2;

    /// <summary>
    /// Longest search term that will be honoured, matching the longest address
    /// the registration endpoint accepts.
    /// </summary>
    private const int MaxSearchLength = 254;

    /* ------------------------------------------------------------ directory */

    public static async Task<Results<Ok<AdminUserListResponse>, ProblemHttpResult>> ListAsync(
        UserManager<Sw5eUser> users,
        Sw5eIdentityDbContext store,
        [FromQuery] string? q,
        [FromQuery] string? role,
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = store.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();

            if (term.Length is < MinSearchLength or > MaxSearchLength)
            {
                return AccountProblems.Invalid(
                    $"A search term is between {MinSearchLength} and {MaxSearchLength} " +
                    "characters. Omit it entirely to list every account.");
            }

            // Matched against the normalised address rather than the stored
            // one. Identity keeps NormalizedEmail precisely so that address
            // comparison does not depend on a collation or a culture, and the
            // unique index is over that column — so searching it is both the
            // correct comparison and the one PostgreSQL can serve without
            // evaluating a function over every row.
            //
            // The display name has no such column, so it is matched with an
            // explicit upper() and is a scan. That is the honest cost of
            // offering it, and it is bounded by the page size and by the fact
            // that this is a table of accounts rather than of content.
            var normalized = users.NormalizeEmail(term) ?? term.ToUpperInvariant();
            var upper = term.ToUpperInvariant();

            query = query.Where(user =>
                (user.NormalizedEmail != null && user.NormalizedEmail.Contains(normalized)) ||
                user.DisplayName.ToUpper().Contains(upper));
        }

        if (!string.IsNullOrEmpty(role))
        {
            // Against the closed list, ordinally, exactly as the role
            // assignment endpoint matches. A filter that silently matched
            // nothing would show an administrator an empty directory and let
            // them conclude the platform has no contributors.
            var wanted = Sw5eRoles.All.FirstOrDefault(
                known => string.Equals(known, role, StringComparison.Ordinal));

            if (wanted is null)
            {
                return AccountProblems.Invalid(
                    $"'{role}' is not a role. Roles are: " + string.Join(", ", Sw5eRoles.All) + ".");
            }

            // Expressed as a join through the framework's own role tables
            // rather than by materialising every account and asking the
            // UserManager one at a time. The second is what a first draft looks
            // like and it is a query per row.
            query =
                from user in query
                join membership in store.UserRoles on user.Id equals membership.UserId
                join known in store.Roles on membership.RoleId equals known.Id
                where known.Name == wanted
                select user;
        }

        if (!string.IsNullOrEmpty(status))
        {
            switch (status)
            {
                case "all":
                    // Explicit, so "everything" is something somebody asked for
                    // rather than something an empty parameter caused.
                    break;
                case "suspended":
                    query = query.Where(user => user.SuspendedAt != null);
                    break;
                case "active":
                    query = query.Where(user => user.SuspendedAt == null && user.EmailConfirmed);
                    break;
                case "unverified":
                    // Registrations that never completed. Worth being able to
                    // ask for: they are the accounts an administrator is
                    // looking at when somebody says a verification email never
                    // arrived.
                    query = query.Where(user => !user.EmailConfirmed);
                    break;
                default:
                    return AccountProblems.Invalid(
                        "That is not a status. It must be one of: all, active, suspended, " +
                        "unverified.");
            }
        }

        var (pageNumber, size) = ReadPaging(page, pageSize);

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            // Oldest first, so the directory reads as the order people arrived
            // and a page does not reshuffle under the cursor when somebody
            // registers. The identifier breaks ties, because two accounts
            // created in the same tick would otherwise be free to swap places
            // between two requests for the same page.
            .OrderBy(user => user.CreatedAt)
            .ThenBy(user => user.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var described = await DescribeManyAsync(store, rows, cancellationToken);

        return TypedResults.Ok(new AdminUserListResponse(
            described,
            pageNumber,
            size,
            total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)size)));
    }

    /* --------------------------------------------------------- one account */

    public static async Task<Results<Ok<AdminUserDetail>, ProblemHttpResult>> GetAsync(
        Guid userId,
        Sw5eIdentityDbContext store,
        [FromServices] IContentAuthoringStore? authoring,
        CancellationToken cancellationToken)
    {
        var user = await store.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null)
        {
            return AccountProblems.NoSuchAccount;
        }

        var described = await DescribeManyAsync(store, [user], cancellationToken);

        // Null rather than zero when authoring is not registered at all — a
        // file-backed deployment has no drafts and never will, and reporting
        // zero would invite an interface to draw "0 drafts" beside an account
        // on a deployment where the concept does not exist.
        int? drafts = null;

        if (authoring is not null)
        {
            var all = await authoring.ListDraftsAsync(cancellationToken);
            drafts = all.Count(draft => draft.CreatedByUserId == userId);
        }

        return TypedResults.Ok(new AdminUserDetail(described[0], drafts));
    }

    /* -------------------------------------------------------------- the log */

    public static async Task<Results<Ok<AdministrativeLogResponse>, ProblemHttpResult>> ListActionsAsync(
        Sw5eIdentityDbContext store,
        [FromQuery] Guid? subjectId,
        [FromQuery] Guid? actorId,
        [FromQuery] string? action,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = store.AdministrativeActions.AsNoTracking();

        if (subjectId is { } subject)
        {
            query = query.Where(entry => entry.SubjectUserId == subject);
        }

        if (actorId is { } actor)
        {
            query = query.Where(entry => entry.ActorUserId == actor);
        }

        if (!string.IsNullOrEmpty(action))
        {
            if (!AdministrativeActionWire.TryParse(action, out _))
            {
                return AccountProblems.Invalid(
                    "That is not an administrative action. It must be one of: " +
                    string.Join(", ", AdministrativeActionWire.All) + ".");
            }

            query = query.Where(entry => entry.Action == action);
        }

        var (pageNumber, size) = ReadPaging(page, pageSize);

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            // Newest first. An audit log is read from the end: the question is
            // almost always "what has just been done", and the answer to "what
            // was done in 2027" is a filter rather than a page number.
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var actions = rows
            .Select(entry => new AdministrativeActionResponse(
                entry.Id,
                entry.Action,
                entry.ActorUserId,
                entry.ActorDisplayName,
                entry.SubjectUserId,
                entry.SubjectDisplayName,
                AdministrativeLog.Split(entry.RolesBefore),
                AdministrativeLog.Split(entry.RolesAfter),
                entry.Reason,
                entry.CreatedAt))
            .ToArray();

        return TypedResults.Ok(new AdministrativeLogResponse(
            actions,
            pageNumber,
            size,
            total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)size)));
    }

    /* ------------------------------------------------------------- shaping */

    /// <summary>
    /// Describes a page of accounts, in a fixed number of queries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three queries for a page of any size: the accounts themselves, their
    /// role memberships, and whether each holds a passkey. The obvious
    /// alternative — asking the <c>UserManager</c> per account — is two queries
    /// per row, which turns a page of a hundred into two hundred round trips
    /// and makes the cost of this endpoint a function of the page size in the
    /// worst way.
    /// </para>
    /// <para>
    /// The passkey question is answered as a count of accounts that have one
    /// rather than by reading credentials. Nothing administrative needs a
    /// credential identifier: what an administrator needs to know before
    /// granting Contributor is whether the grant will be usable, and that is a
    /// yes or a no. Public keys and signature counters have no business leaving
    /// the store for a directory listing.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<AdminUserSummary>> DescribeManyAsync(
        Sw5eIdentityDbContext store,
        IReadOnlyList<Sw5eUser> accounts,
        CancellationToken cancellationToken)
    {
        if (accounts.Count == 0)
        {
            return [];
        }

        var ids = accounts.Select(user => user.Id).ToArray();

        var memberships = await (
                from membership in store.UserRoles.AsNoTracking()
                join role in store.Roles.AsNoTracking() on membership.RoleId equals role.Id
                where ids.Contains(membership.UserId)
                select new { membership.UserId, role.Name })
            .ToListAsync(cancellationToken);

        var roles = memberships
            .GroupBy(entry => entry.UserId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.Name!)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());

        var withPasskeys = await store
            .Set<IdentityUserPasskey<Guid>>()
            .AsNoTracking()
            .Where(passkey => ids.Contains(passkey.UserId))
            .Select(passkey => passkey.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var enrolled = withPasskeys.ToHashSet();

        return
        [
            .. accounts.Select(user => new AdminUserSummary(
                user.Id,
                user.Email ?? string.Empty,
                user.DisplayName,
                roles.GetValueOrDefault(user.Id, []),
                user.EmailConfirmed,
                user.TwoFactorEnabled,
                user.TwoFactorEnabled || enrolled.Contains(user.Id),

                // Read off the column rather than through
                // UserManager.IsLockedOutAsync, which would be a query per row.
                // The comparison is the same one the framework makes: a lockout
                // is in force while its end is in the future.
                user.LockoutEnabled && user.LockoutEnd > DateTimeOffset.UtcNow,
                Describe(user),
                user.CreatedAt)),
        ];
    }

    /// <summary>An account's suspension, or null when it has none.</summary>
    public static AccountSuspensionResponse? Describe(Sw5eUser user) =>
        user.SuspendedAt is { } at
            ? new AccountSuspensionResponse(at, user.SuspensionReason, user.SuspendedByUserId)
            : null;

    /// <summary>
    /// Reads the paging parameters, clamping rather than refusing.
    /// </summary>
    /// <remarks>
    /// The same rule the flag queue and the content endpoints already follow: a
    /// page past the end is an empty page rather than an error, and the size is
    /// clamped because it is the parameter that decides how much work the
    /// server does.
    /// </remarks>
    private static (int Page, int PageSize) ReadPaging(int? page, int? pageSize) =>
        (Math.Max(page ?? 1, 1),
         Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}
