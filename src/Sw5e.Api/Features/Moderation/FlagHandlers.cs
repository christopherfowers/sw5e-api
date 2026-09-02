using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Sw5e.Api.Security;
using Sw5e.Domain.Content;
using Sw5e.Domain.Moderation;
using Sw5e.Identity;
using Sw5e.Infrastructure.Persistence.Moderation;

namespace Sw5e.Api.Features.Moderation;

/// <summary>
/// Raising reports, reading your own, and working the queue.
/// </summary>
/// <remarks>
/// <para>
/// This is the first place on the platform where a request from somebody who is
/// not a Contributor causes a row to be written, and it is written the way a
/// first write surface ought to be: every value is bounded before it is
/// accepted, the target is proved to exist before anything is stored, the free
/// text is never interpreted, and the row it produces cannot alter a single
/// byte of what the site publishes.
/// </para>
/// <para>
/// Nothing here touches content. A flag is a note attached to a document from
/// the outside, and the code that writes it holds no reference to a content
/// context at all — it holds <see cref="IContentRepository"/>, which is
/// read-only by its interface. That is what makes "a report can never change
/// the reference" a property of the type system rather than a promise.
/// </para>
/// </remarks>
internal static class FlagHandlers
{
    /// <summary>Largest page the queue will hand out.</summary>
    /// <remarks>
    /// Bounded because the page size arrives in a query string. Without a cap,
    /// one request asking for a million rows is a cheap way to make the server
    /// do a great deal of work and to make the moderator's browser stop
    /// responding.
    /// </remarks>
    private const int MaxPageSize = 100;

    private const int DefaultPageSize = 25;

    /// <summary>
    /// How many of the worst-affected documents the summary names.
    /// </summary>
    /// <remarks>
    /// Enough to see the shape of the queue at a glance and not enough to be a
    /// second list to read. The summary's job is to answer "where is the bulk
    /// of this", and twelve rows answers it.
    /// </remarks>
    private const int MostFlaggedCount = 12;

    /* ------------------------------------------------------------ reporting */

    public static async Task<Results<Created<FlagResponse>, ProblemHttpResult>> RaiseAsync(
        RaiseFlagRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        IContentRepository content,
        Sw5eModerationDbContext store,
        IOptions<FlagRateLimitOptions> limits,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return FlagProblems.MissingBody;
        }

        if (await users.GetUserAsync(context.User) is not { } reporter)
        {
            return FlagProblems.NotAuthenticated;
        }

        // Ordered cheapest first: nothing below costs a store read until the
        // request has been shown to be well-formed.
        if (!FlagRequestValidation.TryReadReason(request.Reason, out var reason, out var problem) ||
            !FlagRequestValidation.TryReadTarget(
                request.TargetType, request.TargetKey, out var type, out var key, out problem) ||
            !FlagRequestValidation.TryReadKind(reason, type, out var kind, out problem) ||
            !FlagRequestValidation.TryReadText(
                request.Details,
                "details",
                ContentFlagRules.MaxDetailsLength,
                ContentFlagRules.RequiresDetails(reason),
                out var details,
                out problem))
        {
            return problem!;
        }

        // The target has to exist. A report pointing at nothing can never be
        // reviewed and can never be closed, so accepting one would be accepting
        // a row that will sit in the queue forever — which is also the cheapest
        // way for somebody with an account to fill it.
        //
        // The document is read rather than merely counted, because its name is
        // copied onto the flag. See ContentFlagRow.TargetName.
        var document = await content.GetAsync(type, key, cancellationToken);

        if (document is null)
        {
            return FlagProblems.NoSuchTarget;
        }

        if (await ExceededQuotaAsync(store, reporter.Id, limits.Value, clock, cancellationToken)
            is { } quotaProblem)
        {
            return quotaProblem;
        }

        var flag = new ContentFlagRow
        {
            Id = Guid.CreateVersion7(),
            TargetKind = kind,

            // The registry's own key, never the caller's string: the caller may
            // have addressed the type by its route segment, and two spellings
            // of one type in this column would split the queue's grouping in
            // half without anything looking wrong.
            TargetType = type.Key,
            TargetKey = key,
            TargetName = document.Name,
            Reason = reason,
            Details = details,
            Status = FlagStatus.Open,
            ReporterUserId = reporter.Id,
            CreatedAt = clock.GetUtcNow(),
        };

        store.ContentFlags.Add(flag);

        try
        {
            await store.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateReport(exception))
        {
            // The unique index caught what a pre-flight check could not: two
            // submissions from one account arriving together both find nothing
            // and both insert. This is the only place that race can be settled.
            return FlagProblems.AlreadyReported;
        }

        // Logged without the free text. The queue is where a report is read;
        // the log is where it is counted, and copying user-submitted prose into
        // a log stream that fans out to a shared aggregator is how it ends up
        // rendered somewhere nobody escaped it.
        loggerFactory.CreateLogger(LogCategories.Moderation).LogInformation(
            "Flag {FlagId} raised against {TargetType}/{TargetKey} for {Reason} by account {UserId}.",
            flag.Id,
            flag.TargetType,
            flag.TargetKey,
            FlagWire.NameOf(flag.Reason),
            reporter.Id);

        // 201 with the stored report and no Location header. There is no route
        // that serves one report on its own — a reporter reads theirs at
        // /api/flags/mine and a reviewer reads the queue — so a Location would
        // name an address nothing answers, which is worse than omitting it. The
        // body already carries everything a caller could have fetched from one.
        return TypedResults.Created(
            (string?)null,
            Describe(
                flag,
                new FlagAccountResponse(reporter.Id, reporter.DisplayName),
                reviewer: null,
                includeReviewerNote: false));
    }

    /* ------------------------------------------------------- your own reports */

    public static async Task<Results<Ok<FlagListResponse>, ProblemHttpResult>> ListMineAsync(
        HttpContext context,
        UserManager<Sw5eUser> users,
        Sw5eModerationDbContext store,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        if (await users.GetUserAsync(context.User) is not { } reporter)
        {
            return FlagProblems.NotAuthenticated;
        }

        var (pageNumber, size) = ReadPaging(page, pageSize);

        var query = store.ContentFlags
            .AsNoTracking()
            .Where(flag => flag.ReporterUserId == reporter.Id);

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(flag => flag.CreatedAt)
            .ThenBy(flag => flag.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var reporterAccount = new FlagAccountResponse(reporter.Id, reporter.DisplayName);

        // Reviewer notes are withheld here, and the reviewer's identity with
        // them. A note is written between the people working the queue; a
        // reporter is told what happened to their report and when, which is the
        // part that concerns them.
        var flags = rows
            .Select(flag => Describe(flag, reporterAccount, null, includeReviewerNote: false))
            .ToArray();

        return TypedResults.Ok(Page(flags, pageNumber, size, total));
    }

    /* ---------------------------------------------------------------- queue */

    public static async Task<Results<Ok<FlagListResponse>, ProblemHttpResult>> ListAsync(
        UserManager<Sw5eUser> users,
        Sw5eModerationDbContext store,
        [FromQuery] string? status,
        [FromQuery] string? reason,
        [FromQuery] string? targetKind,
        [FromQuery] string? targetType,
        [FromQuery] string? targetKey,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = store.ContentFlags.AsNoTracking();

        // Every filter is optional and every one of them is parsed rather than
        // pasted. An unrecognised value is a 400 and not "no filter": silently
        // ignoring a filter the caller asked for shows them the whole queue
        // while they believe they are looking at one slice of it, which on a
        // moderation queue means acting on the wrong row.
        //
        // The default is the worklist rather than everything. A queue that
        // opens on every report ever filed, most of them long since resolved,
        // is a queue whose first page is useless.
        if (string.IsNullOrEmpty(status))
        {
            query = query.Where(flag =>
                flag.Status == FlagStatus.Open || flag.Status == FlagStatus.Accepted);
        }
        else if (string.Equals(status, "all", StringComparison.Ordinal))
        {
            // Explicit, so that "show me everything" is something somebody
            // asked for rather than something an empty parameter caused.
        }
        else if (FlagWire.TryParseStatus(status, out var wanted))
        {
            query = query.Where(flag => flag.Status == wanted);
        }
        else
        {
            return FlagProblems.Invalid(
                "status",
                "That is not a status. It must be \"all\" or one of: " +
                string.Join(", ", FlagWire.StatusNames) + ".");
        }

        if (!string.IsNullOrEmpty(reason))
        {
            if (!FlagWire.TryParseReason(reason, out var wanted))
            {
                return FlagProblems.Invalid(
                    "reason",
                    "That is not a reason. It must be one of: " +
                    string.Join(", ", FlagWire.ReasonNames) + ".");
            }

            query = query.Where(flag => flag.Reason == wanted);
        }

        if (!string.IsNullOrEmpty(targetKind))
        {
            if (!FlagWire.TryParseTargetKind(targetKind, out var wanted))
            {
                return FlagProblems.Invalid(
                    "targetKind", "That is not a target kind. It must be \"document\" or \"image\".");
            }

            query = query.Where(flag => flag.TargetKind == wanted);
        }

        if (!string.IsNullOrEmpty(targetType))
        {
            if (!ContentTypeRegistry.TryResolve(targetType, out var resolved))
            {
                return FlagProblems.Invalid(
                    "targetType", "That is not a content type this site serves.");
            }

            // The registry's key, so that a caller filtering by route segment
            // sees the same rows as one filtering by canonical key.
            var wanted = resolved.Key;
            query = query.Where(flag => flag.TargetType == wanted);

            if (!string.IsNullOrEmpty(targetKey))
            {
                if (!ContentSlug.IsValid(targetKey))
                {
                    return FlagProblems.Invalid(
                        "targetKey",
                        "A content key is lowercase letters and digits in hyphen-separated groups.");
                }

                query = query.Where(flag => flag.TargetKey == targetKey);
            }
        }
        else if (!string.IsNullOrEmpty(targetKey))
        {
            // A key without a type is ambiguous — keys are only unique within a
            // type — so it is refused rather than answered with rows from
            // whichever types happen to share the key.
            return FlagProblems.Invalid(
                "targetType", "Filtering by key also needs the type the key belongs to.");
        }

        var (pageNumber, size) = ReadPaging(page, pageSize);

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            // Rights complaints first, then everything else newest first. The
            // ordering is the queue's only opinion and it is a deliberate one:
            // somebody saying their work is being published without permission
            // is the report that must not wait behind two hundred requests to
            // redraw a portrait. See FlagReason.ImageRightsComplaint.
            .OrderByDescending(flag => flag.Reason == FlagReason.ImageRightsComplaint)
            .ThenByDescending(flag => flag.CreatedAt)
            .ThenBy(flag => flag.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var accounts = await ResolveAccountsAsync(users, rows, cancellationToken);

        var flags = rows
            .Select(flag => Describe(
                flag,
                accounts[flag.ReporterUserId],
                flag.ReviewedByUserId is { } reviewer ? accounts[reviewer] : null,
                includeReviewerNote: true))
            .ToArray();

        return TypedResults.Ok(Page(flags, pageNumber, size, total));
    }

    public static async Task<Ok<FlagSummaryResponse>> SummariseAsync(
        Sw5eModerationDbContext store,
        CancellationToken cancellationToken)
    {
        var byStatus = await store.ContentFlags
            .AsNoTracking()
            .GroupBy(flag => flag.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var counts = byStatus.ToDictionary(entry => entry.Status, entry => entry.Count);

        var outstanding = store.ContentFlags
            .AsNoTracking()
            .Where(flag =>
                flag.Status == FlagStatus.Open || flag.Status == FlagStatus.Accepted);

        var byReason = await outstanding
            .GroupBy(flag => flag.Reason)
            .Select(group => new { Reason = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // The line that stops a hundred and fifty attribution reports reading
        // as a hundred and fifty separate problems.
        //
        // Grouped and cut in the database rather than in memory: the whole
        // point is that this page stays cheap when the queue is long, and
        // materialising every outstanding row to count them here would make it
        // the most expensive page on the site precisely when it matters.
        //
        // `Max` picks the recorded name rather than the newest one. They are
        // the same string in every realistic case — a document's name would
        // have to change between two reports for them to differ — and an
        // aggregate is deterministic, translatable, and cannot make two
        // identical requests answer differently.
        var grouped = await outstanding
            .GroupBy(flag => new { flag.TargetKind, flag.TargetType, flag.TargetKey })
            .Select(group => new
            {
                group.Key.TargetKind,
                group.Key.TargetType,
                group.Key.TargetKey,
                TargetName = group.Max(flag => flag.TargetName),
                Count = group.Count(),
            })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.TargetType)
            .ThenBy(entry => entry.TargetKey)
            .Take(MostFlaggedCount)
            .ToListAsync(cancellationToken);

        var mostFlagged = grouped
            .Select(entry => new FlagTargetSummaryResponse(
                FlagWire.NameOf(entry.TargetKind),
                entry.TargetType,
                entry.TargetKey,
                // Max over a group is only nullable because Max over an empty
                // sequence is; a group produced by GroupBy is never empty, and
                // the column itself is not nullable.
                entry.TargetName!,
                entry.Count))
            .ToArray();

        return TypedResults.Ok(new FlagSummaryResponse(
            Total: counts.Values.Sum(),
            Outstanding:
                counts.GetValueOrDefault(FlagStatus.Open) +
                counts.GetValueOrDefault(FlagStatus.Accepted),

            // Every status, including the empty ones: the queue draws a row per
            // state and a missing "declined" would read as a page that failed
            // to load rather than as a count of zero.
            ByStatus:
            [
                .. FlagWire.StatusNames.Select(name =>
                {
                    FlagWire.TryParseStatus(name, out var value);
                    return new FlagCountResponse(name, counts.GetValueOrDefault(value));
                }),
            ],

            // Reasons, in the taxonomy's own order rather than by count, and
            // only the ones with something outstanding. Order first because a
            // list that reshuffles as work is done is a list nobody builds a
            // habit around.
            ByReason:
            [
                .. FlagWire.ReasonNames
                    .Select(name =>
                    {
                        FlagWire.TryParseReason(name, out var value);
                        return new FlagCountResponse(
                            name,
                            byReason.FirstOrDefault(entry => entry.Reason == value)?.Count ?? 0);
                    })
                    .Where(entry => entry.Count > 0),
            ],

            MostFlagged: mostFlagged));
    }

    public static async Task<Results<Ok<FlagResponse>, ProblemHttpResult>> UpdateStatusAsync(
        Guid flagId,
        UpdateFlagStatusRequest? request,
        HttpContext context,
        UserManager<Sw5eUser> users,
        Sw5eModerationDbContext store,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return FlagProblems.MissingBody;
        }

        if (await users.GetUserAsync(context.User) is not { } reviewer)
        {
            return FlagProblems.NotAuthenticated;
        }

        if (!FlagRequestValidation.TryReadStatus(request.Status, out var wanted, out var problem) ||
            !FlagRequestValidation.TryReadText(
                request.Note,
                "note",
                ContentFlagRules.MaxReviewerNoteLength,
                required: false,
                out var note,
                out problem))
        {
            return problem!;
        }

        var flag = await store.ContentFlags
            .FirstOrDefaultAsync(candidate => candidate.Id == flagId, cancellationToken);

        if (flag is null)
        {
            return FlagProblems.NoSuchFlag;
        }

        if (!ContentFlagRules.CanTransition(flag.Status, wanted))
        {
            return FlagProblems.BadTransition(
                FlagWire.NameOf(flag.Status), FlagWire.NameOf(wanted));
        }

        var previous = flag.Status;

        flag.Status = wanted;
        flag.ReviewedByUserId = reviewer.Id;
        flag.ReviewedAt = clock.GetUtcNow();

        // A note is replaced only when one was sent. Blanking somebody else's
        // explanation because this request did not carry one would quietly
        // destroy the queue's only record of why a report was declined.
        if (note is not null)
        {
            flag.ReviewerNote = note;
        }

        try
        {
            await store.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateReport(exception))
        {
            // Reopening a report the same account has since raised again would
            // put two outstanding copies of one report in the queue, which the
            // unique index refuses. Reported as the conflict it is rather than
            // as a 500.
            return FlagProblems.AlreadyReported;
        }

        loggerFactory.CreateLogger(LogCategories.Moderation).LogInformation(
            "Flag {FlagId} moved from {From} to {To} by account {UserId}.",
            flag.Id,
            FlagWire.NameOf(previous),
            FlagWire.NameOf(wanted),
            reviewer.Id);

        var accounts = await ResolveAccountsAsync(users, [flag], cancellationToken);

        return TypedResults.Ok(Describe(
            flag,
            accounts[flag.ReporterUserId],
            new FlagAccountResponse(reviewer.Id, reviewer.DisplayName),
            includeReviewerNote: true));
    }

    /* --------------------------------------------------------------- shared */

    /// <summary>
    /// Checks the two per-account budgets, and returns the refusal if either is
    /// spent.
    /// </summary>
    /// <remarks>
    /// Both counts run before the insert rather than being enforced by a
    /// constraint, because neither is a property of a single row. They are
    /// racy by nature — two concurrent submissions can both see the count one
    /// under the limit — and that is acceptable here in a way it would not be
    /// for the duplicate check: the worst outcome is one report over a soft
    /// ceiling, where the worst outcome there is two copies of one report in
    /// the queue forever.
    /// </remarks>
    private static async Task<ProblemHttpResult?> ExceededQuotaAsync(
        Sw5eModerationDbContext store,
        Guid reporterId,
        FlagRateLimitOptions limits,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow().AddDays(-1);

        var today = await store.ContentFlags
            .AsNoTracking()
            .CountAsync(
                flag => flag.ReporterUserId == reporterId && flag.CreatedAt >= since,
                cancellationToken);

        if (today >= limits.AccountReportsPerDay)
        {
            return FlagProblems.QuotaReached(
                "You have filed as many reports as one account may in a day. The ones you have " +
                "already filed are safe; try again tomorrow.");
        }

        var outstanding = await store.ContentFlags
            .AsNoTracking()
            .CountAsync(
                flag => flag.ReporterUserId == reporterId &&
                        (flag.Status == FlagStatus.Open || flag.Status == FlagStatus.Accepted),
                cancellationToken);

        if (outstanding >= limits.AccountOutstandingReports)
        {
            return FlagProblems.QuotaReached(
                "You have as many reports waiting for review as one account may have at once. " +
                "You can file again once some of them have been looked at.");
        }

        return null;
    }

    /// <summary>
    /// Looks up the display names for one page of flags, in one query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative — a foreign key and a join — is not available: the
    /// moderation schema deliberately has no constraint into identity, because
    /// a deployment may put account data in a database of its own. So the
    /// lookup is explicit, batched over the page rather than per row, and it
    /// answers null for an identifier that no longer matches an account.
    /// </para>
    /// <para>
    /// Nothing but the identifier and the display name leaves this method. The
    /// queue is read by Contributors; being trusted with content does not make
    /// somebody entitled to the email address of everyone who ever reported a
    /// typo.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<Guid, FlagAccountResponse>> ResolveAccountsAsync(
        UserManager<Sw5eUser> users,
        IReadOnlyCollection<ContentFlagRow> flags,
        CancellationToken cancellationToken)
    {
        var ids = flags
            .Select(flag => flag.ReporterUserId)
            .Concat(flags.Where(flag => flag.ReviewedByUserId is not null)
                         .Select(flag => flag.ReviewedByUserId!.Value))
            .Distinct()
            .ToArray();

        Dictionary<Guid, FlagAccountResponse> found = [];

        if (ids.Length > 0)
        {
            found = await users.Users
                .AsNoTracking()
                .Where(user => ids.Contains(user.Id))
                .Select(user => new FlagAccountResponse(user.Id, user.DisplayName))
                .ToDictionaryAsync(account => account.Id, cancellationToken);
        }

        // An identifier with no account behind it is a real state — the report
        // outlived the person who filed it — so it resolves to a named absence
        // rather than to a missing key the caller has to handle.
        return ids.ToDictionary(
            id => id,
            id => found.GetValueOrDefault(id) ?? new FlagAccountResponse(id, null));
    }

    private static FlagResponse Describe(
        ContentFlagRow flag,
        FlagAccountResponse reporter,
        FlagAccountResponse? reviewer,
        bool includeReviewerNote) =>
        new(
            flag.Id,
            FlagWire.NameOf(flag.TargetKind),
            flag.TargetType,
            flag.TargetKey,
            flag.TargetName,
            FlagWire.NameOf(flag.Reason),
            flag.Details,
            FlagWire.NameOf(flag.Status),
            flag.CreatedAt,
            reporter,
            flag.ReviewedAt,
            reviewer,
            includeReviewerNote ? flag.ReviewerNote : null);

    private static FlagListResponse Page(
        IReadOnlyList<FlagResponse> flags,
        int page,
        int pageSize,
        int total) =>
        new(
            flags,
            page,
            pageSize,
            total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));

    /// <summary>
    /// Reads the paging parameters, clamping rather than refusing.
    /// </summary>
    /// <remarks>
    /// A page past the end is an empty page and not an error, which is what the
    /// content endpoints already do — a pager that lands one past the last page
    /// should show nothing, not a failure. The size is clamped because it is
    /// the parameter that decides how much work the server does.
    /// </remarks>
    private static (int Page, int PageSize) ReadPaging(int? page, int? pageSize) =>
        (Math.Max(page ?? 1, 1),
         Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    /// <summary>
    /// Whether a save failed because the outstanding-report index refused a
    /// duplicate.
    /// </summary>
    /// <remarks>
    /// Matched on the index name rather than on the SQLSTATE alone, so that a
    /// unique violation from some future constraint is not silently reported to
    /// the caller as "you already reported this". Being wrong about which
    /// constraint fired is how a real bug gets a friendly message and is never
    /// investigated.
    /// </remarks>
    private static bool IsDuplicateReport(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_content_flag_outstanding_per_reporter",
        };
}
