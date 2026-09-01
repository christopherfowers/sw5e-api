using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Domain.Moderation;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Moderation;

/// <summary>
/// The review queue: who may open it, and what a reviewer may do to a report.
/// </summary>
[Collection(AccountTestCollection.Name)]
public sealed class FlagQueueTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);

        // One container is shared by every test class in this collection and
        // nothing resets it between them, so the table is emptied here rather
        // than every assertion below being written to tolerate somebody else's
        // rows. See FlagFlow.ClearAsync.
        await FlagFlow.ClearAsync(_factory);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAnonymousCallerCannotOpenTheQueue()
    {
        var client = _factory.CreateBrowserClient();

        (await client.GetAsync("/api/flags")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        (await client.GetAsync("/api/flags/summary")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACommunityAccountCannotOpenTheQueue()
    {
        var reporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reporter, "community-queue");

        // It can file, and it can read what it filed.
        await FlagFlow.RaiseAcceptedAsync(reporter, "text-error", details: "A typo.");
        (await reporter.GetAsync("/api/flags/mine")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Forbidden rather than unauthorized: the caller is known and is not
        // permitted. Answering 401 would tell them to sign in again, which they
        // already have.
        (await reporter.GetAsync("/api/flags")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        (await reporter.GetAsync("/api/flags/summary")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ACommunityAccountCannotMoveAReportThroughTheLifecycle()
    {
        var reporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reporter, "community-triage");

        var flagId = await FlagFlow.RaiseAcceptedAsync(
            reporter, "text-error", details: "A typo.");

        // Including their own report. Reporting something is not a claim to
        // decide what happens to it.
        var response = await reporter.PutAsJsonAsync(
            $"/api/flags/{flagId}/status", new { status = "resolved" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Refused rather than done and then reported as refused.
        (await FlagFlow.StoredAsync(_factory, flagId)).Status.ShouldBe(FlagStatus.Open);
    }

    [Fact]
    public async Task AContributorWhoSignedInWithAnEmailedCodeCannotOpenTheQueue()
    {
        // The queue carries the display name of everybody who has reported
        // anything and the text of what they wrote, some of which will be
        // people saying their artwork is being published without permission.
        // A session that only proved control of a mailbox does not open that,
        // and this is the check that the requirement reaches the new endpoints
        // rather than only the ones it was written for.
        var client = _factory.CreateBrowserClient();
        var contributor = await FlagFlow.SignInWithRoleAsync(
            _factory, client, "weak-contributor", Sw5eRoles.Contributor);

        (await client.GetAsync("/api/flags")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await client.PostAsync("/api/auth/logout", content: null);

        var weak = _factory.CreateBrowserClient();
        await weak.PostAsJsonAsync(
            "/api/auth/email/code", new { email = contributor.EmailAddress });

        var signIn = await weak.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new
            {
                email = contributor.EmailAddress,
                code = _factory.Email.LatestSignInCode(contributor.EmailAddress),
            });

        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The session is real — the account area is reachable, which is the
        // whole point of the weaker door existing.
        (await weak.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // And it is still allowed to report things, because reporting is not
        // the privilege being protected.
        (await weak.GetAsync("/api/flags/mine")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await weak.GetAsync("/api/flags");

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Named, so the browser can offer to enrol a passkey rather than
        // telling somebody two clicks from the answer to give up.
        (await refused.ReadJsonAsync()).GetProperty("code").GetString()
            .ShouldBe("strong-authentication-required");
    }

    [Fact]
    public async Task AcceptingARecordsWhoActedAndWhen()
    {
        var reporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reporter, "lifecycle-reporter");

        var flagId = await FlagFlow.RaiseAcceptedAsync(
            reporter,
            "image-artist-known",
            FlagFlow.ImageType,
            FlagFlow.ImageKey,
            "Drawn by someone who is still contactable.");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "lifecycle-reviewer", Sw5eRoles.Contributor);

        var accepted = await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status",
            new { status = "accepted", note = "Confirmed against the artist's portfolio." });

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await accepted.ReadJsonAsync();

        body.GetProperty("status").GetString().ShouldBe("accepted");
        body.GetProperty("reviewedBy").GetProperty("displayName").GetString()
            .ShouldBe("Test lifecycle-reviewer");
        body.GetProperty("reviewedAt").ValueKind
            .ShouldNotBe(System.Text.Json.JsonValueKind.Null);

        var stored = await FlagFlow.StoredAsync(_factory, flagId);

        stored.Status.ShouldBe(FlagStatus.Accepted);
        stored.ReviewedByUserId.ShouldNotBeNull();
        stored.ReviewedAt.ShouldNotBeNull();
        stored.ReviewerNote.ShouldBe("Confirmed against the artist's portfolio.");

        // Accepted is still outstanding. That is the state's entire reason for
        // existing: a reviewer who agrees with two hundred attribution reports
        // in an evening must be able to record that without also having to fix
        // two hundred pictures.
        var queue = await reviewer.GetAsync("/api/flags");
        FlagFlow.FlagsIn(await queue.ReadJsonAsync()).Length.ShouldBe(1);

        var resolved = await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status", new { status = "resolved" });

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);

        // And once it is finished it leaves the default view.
        var afterwards = await reviewer.GetAsync("/api/flags");
        FlagFlow.FlagsIn(await afterwards.ReadJsonAsync()).ShouldBeEmpty();

        // But it has not gone anywhere.
        var everything = await reviewer.GetAsync("/api/flags?status=all");
        FlagFlow.FlagsIn(await everything.ReadJsonAsync()).Length.ShouldBe(1);
    }

    [Fact]
    public async Task ADeclinedReportCannotJumpStraightToResolved()
    {
        var reporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reporter, "transition-reporter");

        var flagId = await FlagFlow.RaiseAcceptedAsync(
            reporter, "content-incorrect", details: "The saving throw is wrong.");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "transition-reviewer", Sw5eRoles.Administrator);

        (await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status",
            new { status = "declined", note = "Matches the book." }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // "Resolved" claims work was done on something a reviewer has just said
        // needed none. A queue that allows it has a status field nobody can
        // trust afterwards.
        var refused = await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status", new { status = "resolved" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadJsonAsync()).GetProperty("code").GetString()
            .ShouldBe("invalid-transition");

        (await FlagFlow.StoredAsync(_factory, flagId)).Status.ShouldBe(FlagStatus.Declined);

        // Reopening is allowed, because reviewers are wrong sometimes and a
        // queue with no way back is one people are afraid to triage quickly.
        (await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status",
            new { status = "open", note = "Reopened: the errata say otherwise." }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FlagFlow.StoredAsync(_factory, flagId)).Status.ShouldBe(FlagStatus.Open);
    }

    [Fact]
    public async Task RestatingTheCurrentStatusIsRefused()
    {
        var reporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reporter, "restate-reporter");

        var flagId = await FlagFlow.RaiseAcceptedAsync(reporter, "text-error");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "restate-reviewer", Sw5eRoles.Contributor);

        // Almost always a double submit or two reviewers on one row. Answering
        // 200 to the second would tell somebody they did something they did not
        // do.
        (await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status", new { status = "open" }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ANonexistentReportIsNotFound()
    {
        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "missing-flag", Sw5eRoles.Contributor);

        (await reviewer.PutAsJsonAsync(
            $"/api/flags/{Guid.NewGuid()}/status", new { status = "accepted" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ManyReportsAgainstOnePictureCollapseIntoOneSummaryRow()
    {
        // The failure this defends against is specific. Roughly a hundred and
        // fifty of the site's pictures carry no recorded artist, so the queue's
        // first day is a wall of near-identical attribution reports with the
        // occasional typo correction buried in it. Paging through in date order
        // is how the typo is never seen.
        for (var index = 0; index < 4; index++)
        {
            var client = _factory.CreateBrowserClient();
            await FlagFlow.SignInAsync(_factory, client, $"crowd-{index}");

            await FlagFlow.RaiseAcceptedAsync(
                client,
                "image-attribution-missing",
                FlagFlow.ImageType,
                FlagFlow.ImageKey,
                "No artist is recorded for this one.");
        }

        var typoReporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, typoReporter, "typo");
        await FlagFlow.RaiseAcceptedAsync(typoReporter, "text-error", details: "Missing comma.");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "summary-reviewer", Sw5eRoles.Contributor);

        var response = await reviewer.GetAsync("/api/flags/summary");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();

        body.GetProperty("outstanding").GetInt32().ShouldBe(5);

        var mostFlagged = body.GetProperty("mostFlagged").EnumerateArray().ToArray();

        // Five reports, two rows. That is the whole point.
        mostFlagged.Length.ShouldBe(2);
        mostFlagged[0].GetProperty("targetKey").GetString().ShouldBe(FlagFlow.ImageKey);
        mostFlagged[0].GetProperty("outstandingCount").GetInt32().ShouldBe(4);
        mostFlagged[0].GetProperty("targetKind").GetString().ShouldBe("image");

        // And the one typo report is not averaged away: it is its own bucket by
        // reason, which is how somebody with ten minutes finds it.
        var byReason = body.GetProperty("byReason").EnumerateArray()
            .ToDictionary(
                entry => entry.GetProperty("key").GetString()!,
                entry => entry.GetProperty("count").GetInt32());

        byReason["image-attribution-missing"].ShouldBe(4);
        byReason["text-error"].ShouldBe(1);

        // Reasons with nothing outstanding are omitted rather than listed at
        // zero: this is a worklist, and eight rows of nothing is seven rows of
        // noise.
        byReason.ShouldNotContainKey("image-rights-complaint");

        // Filtering by reason answers the same numbers, so the summary and the
        // list cannot drift apart.
        var filtered = await reviewer.GetAsync("/api/flags?reason=text-error");
        FlagFlow.FlagsIn(await filtered.ReadJsonAsync()).Length.ShouldBe(1);
    }

    [Fact]
    public async Task ARightsComplaintSortsAheadOfEverythingElse()
    {
        // Somebody saying their work is being published without permission has
        // a clock on it and an obligation attached. It must not queue behind
        // two hundred requests to redraw a portrait, and date order alone would
        // put it wherever it happened to land.
        var earlier = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, earlier, "rights-complainant");

        await FlagFlow.RaiseAcceptedAsync(
            earlier,
            "image-rights-complaint",
            FlagFlow.ImageType,
            FlagFlow.ImageKey,
            "I drew this and did not license it to anybody.");

        for (var index = 0; index < 3; index++)
        {
            var later = _factory.CreateBrowserClient();
            await FlagFlow.SignInAsync(_factory, later, $"rights-noise-{index}");
            await FlagFlow.RaiseAcceptedAsync(later, "text-error", details: "Small typo.");
        }

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "rights-reviewer", Sw5eRoles.Contributor);

        var queue = await reviewer.GetAsync("/api/flags");
        var flags = FlagFlow.FlagsIn(await queue.ReadJsonAsync());

        flags.Length.ShouldBe(4);

        // First despite being the oldest, which is the only way to tell this
        // apart from a queue that happens to be ordered by date.
        flags[0].GetProperty("reason").GetString().ShouldBe("image-rights-complaint");
    }

    [Fact]
    public async Task AnUnrecognisedFilterIsRefusedRatherThanIgnored()
    {
        var reporter = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reporter, "filter-reporter");
        await FlagFlow.RaiseAcceptedAsync(reporter, "text-error", details: "A typo.");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "filter-reviewer", Sw5eRoles.Contributor);

        // Silently ignoring a filter shows a reviewer the whole queue while
        // they believe they are looking at one slice of it, which on a
        // moderation queue means acting on the wrong row.
        (await reviewer.GetAsync("/api/flags?status=nonsense")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        (await reviewer.GetAsync("/api/flags?reason=nonsense")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        // A key without the type it belongs to is ambiguous — keys are unique
        // within a type and not across them — so it is refused rather than
        // answered with rows from whichever types happen to share it.
        (await reviewer.GetAsync($"/api/flags?targetKey={FlagFlow.DocumentKey}")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        // The route segment and the canonical key name the same type, so
        // filtering by either has to answer the same rows.
        var bySegment = await reviewer.GetAsync(
            $"/api/flags?targetType=species&targetKey={FlagFlow.DocumentKey}");

        FlagFlow.FlagsIn(await bySegment.ReadJsonAsync()).Length.ShouldBe(1);
    }
}
