using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Domain.Moderation;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Moderation;

/// <summary>
/// Filing a report: who may, what is accepted, and — mostly — what is refused.
/// </summary>
/// <remarks>
/// This is the platform's first endpoint that writes a row on behalf of someone
/// who is not a Contributor, so the majority of what follows asserts that
/// something did <em>not</em> happen. Every refusal is checked against the
/// table as well as against the status line: an endpoint that answers 403 after
/// writing is indistinguishable from one that answers 403 instead of writing,
/// unless somebody looks.
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class ContentFlagTests(PostgresFixture postgres) : IAsyncLifetime
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
    public async Task AnAnonymousCallerCannotFileAReport()
    {
        var client = _factory.CreateBrowserClient();

        var response = await FlagFlow.RaiseAsync(client, "text-error", details: "A typo.");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The assertion with teeth. A 401 issued after the insert would look
        // identical on the wire and would mean anybody could fill the queue.
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AReportFromAnotherSiteIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "csrf-report");

        // A real session, and a request a page on another origin could have
        // caused. The session cookie is SameSite=Strict so a browser would not
        // attach it at all; this proves the second layer holds on its own.
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/flags")
        {
            Content = JsonContent.Create(new
            {
                reason = "text-error",
                targetType = FlagFlow.DocumentType,
                targetKey = FlagFlow.DocumentKey,
                details = "Filed from somewhere else.",
            }),
        };

        forged.Headers.Add("Origin", "https://sw5e-phishing.example");

        var response = await client.SendAsync(forged);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAccountCanReportAPictureItRecognises()
    {
        var client = _factory.CreateBrowserClient();
        var account = await FlagFlow.SignInAsync(_factory, client, "artist-known");

        var response = await FlagFlow.RaiseAsync(
            client,
            "image-artist-known",
            FlagFlow.ImageType,
            FlagFlow.ImageKey,
            "This is by Nadia Ordo; she posted it in 2017 and still has the file.");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.ReadJsonAsync();

        body.GetProperty("status").GetString().ShouldBe("open");
        body.GetProperty("reason").GetString().ShouldBe("image-artist-known");

        // Derived from the reason rather than sent, which is what stops a
        // client contradicting itself.
        body.GetProperty("targetKind").GetString().ShouldBe("image");

        // The document's name is copied onto the report, so the queue reads as
        // something rather than as a key.
        body.GetProperty("targetName").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("reporter").GetProperty("displayName").GetString()
            .ShouldBe("Test artist-known");

        var stored = (await FlagFlow.StoredAsync(_factory)).ShouldHaveSingleItem();

        stored.Reason.ShouldBe(FlagReason.ImageArtistKnown);
        stored.TargetKind.ShouldBe(FlagTargetKind.Image);
        stored.TargetType.ShouldBe(FlagFlow.ImageType);
        stored.TargetKey.ShouldBe(FlagFlow.ImageKey);
        stored.Status.ShouldBe(FlagStatus.Open);
        stored.ReviewedByUserId.ShouldBeNull();

        // And it belongs to the account that filed it rather than to whoever
        // the handler happened to resolve.
        (await FlagFlow.StoredCountAsync(_factory, account.EmailAddress)).ShouldBe(1);
    }

    [Fact]
    public async Task AReportPointingAtNothingIsRefusedRatherThanStored()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "phantom-target");

        // Well-formed in every respect except that the site publishes no such
        // document. A report against one could never be reviewed — there is
        // nothing to look at — and could never be closed, so it would sit in
        // the queue forever. Accepting them is the cheapest way for anybody
        // with an account to bury the moderators.
        var response = await FlagFlow.RaiseAsync(
            client, "content-incorrect", "species", "no-such-species-exists");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnknownContentTypeIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "phantom-type");

        // The type is resolved against the compiled registry before it reaches
        // any store, because it ends up in a filesystem path join in one
        // implementation and a table selection in the other.
        var response = await FlagFlow.RaiseAsync(
            client, "content-incorrect", "../../etc", "passwd");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task APictureReasonCannotBeRaisedAgainstWriting()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "wrong-kind");

        // "I know who drew this" is not a statement anybody can make about a
        // species entry, and a queue that carried it would carry a row no
        // reviewer could act on.
        var response = await FlagFlow.RaiseAsync(
            client, "image-artist-known", FlagFlow.DocumentType, FlagFlow.DocumentKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWritingReasonCannotBeRaisedAgainstAPicture()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "wrong-kind-reverse");

        var response = await FlagFlow.RaiseAsync(
            client, "text-error", FlagFlow.ImageType, FlagFlow.ImageKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherWithNothingWrittenUnderItIsRefused()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "empty-other");

        var response = await FlagFlow.RaiseAsync(
            client, "other", FlagFlow.DocumentType, FlagFlow.DocumentKey, "   ");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Named, so the browser client can put the message beside the textarea
        // rather than at the top of the form.
        (await response.ReadJsonAsync())
            .GetProperty("fieldErrors").TryGetProperty("details", out _).ShouldBeTrue();

        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task FreeTextPastTheLimitIsRefusedRatherThanTruncated()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "long-details");

        var response = await FlagFlow.RaiseAsync(
            client,
            "text-error",
            FlagFlow.DocumentType,
            FlagFlow.DocumentKey,
            new string('a', ContentFlagRules.MaxDetailsLength + 1));

        // Refused, not silently cut down. A truncated report reads as a
        // complete one and loses whatever the reporter put at the end of it.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();

        // And the boundary itself is accepted, so the limit is a limit rather
        // than an off-by-one nobody noticed.
        var atTheLimit = await FlagFlow.RaiseAsync(
            client,
            "text-error",
            FlagFlow.DocumentType,
            FlagFlow.DocumentKey,
            new string('a', ContentFlagRules.MaxDetailsLength));

        atTheLimit.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task InvisibleControlCharactersAreRefused()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "control-characters");

        // A right-to-left override is invisible in every interface a reviewer
        // will use and can reverse the apparent meaning of the sentence they
        // are about to act on.
        var response = await FlagFlow.RaiseAsync(
            client,
            "text-error",
            FlagFlow.DocumentType,
            FlagFlow.DocumentKey,
            "This looks harmless" + '\u202E' + "but it is not.");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await FlagFlow.StoredAsync(_factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewlineInFreeTextIsKept()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "newlines");

        // The counterweight to the test above. A textarea sends \r\n, and a
        // rule that rejected every multi-line report over it would be absurd.
        var response = await FlagFlow.RaiseAsync(
            client,
            "text-error",
            FlagFlow.DocumentType,
            FlagFlow.DocumentKey,
            "First line.\r\nSecond line.");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var stored = (await FlagFlow.StoredAsync(_factory)).ShouldHaveSingleItem();

        stored.Details.ShouldBe("First line.\nSecond line.");
    }

    [Fact]
    public async Task MarkupInFreeTextIsStoredVerbatimAndEscapedOnTheWayOut()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "markup");

        const string Payload =
            "<script>alert(document.cookie)</script> and \"><img src=x onerror=alert(1)>";

        var created = await FlagFlow.RaiseAsync(
            client, "text-error", FlagFlow.DocumentType, FlagFlow.DocumentKey, Payload);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Stored exactly as written. Encoding at rest is the classic mistake:
        // it makes the column's contents depend on which writer inserted them,
        // double-encodes the moment anything re-encodes on output, and it
        // mangles the ordinary sentences this field exists to collect.
        var stored = (await FlagFlow.StoredAsync(_factory)).ShouldHaveSingleItem();
        stored.Details.ShouldBe(Payload);

        // And escaped on the way out. This asserts on the bytes rather than on
        // the parsed value, because the parsed value is identical either way —
        // it is the raw response a browser would receive that decides whether
        // this is a stored cross-site scripting hole in a page belonging to a
        // Contributor or an Administrator.
        var raw = await created.Content.ReadAsStringAsync();

        raw.ShouldNotContain("<script>");
        raw.ShouldNotContain("<img");
        raw.ShouldContain("\\u003C");

        // The queue renders the same value to a reviewer, so it is checked
        // there too rather than only on the reply to the person who filed it.
        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "markup-reviewer", Sw5eRoles.Contributor);

        var queue = await reviewer.GetAsync("/api/flags");
        var queueBody = await queue.Content.ReadAsStringAsync();

        queue.StatusCode.ShouldBe(HttpStatusCode.OK);
        queueBody.ShouldNotContain("<script>");
        queueBody.ShouldNotContain("<img");

        // Escaped, not lost: the reviewer has to be able to read what was
        // actually reported.
        var parsed = FlagFlow.FlagsIn(await queue.ReadJsonAsync()).ShouldHaveSingleItem();
        parsed.GetProperty("details").GetString().ShouldBe(Payload);
    }

    [Fact]
    public async Task ADisplayNameFullOfMarkupIsEscapedInTheQueueToo()
    {
        // The free text is the obvious untrusted field and it is not the only
        // one. A display name is chosen by the account holder, is carried onto
        // every report they file, and is rendered to reviewers beside it.
        var client = _factory.CreateBrowserClient();
        var address = AccountFlow.NewAddress("markup-name");
        var account = new AccountFlow(client, address, "<img src=x onerror=alert(1)>");

        await account.EstablishAsync(_factory.Email);
        await FlagFlow.RaiseAcceptedAsync(client, "text-error", details: "Ordinary text.");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "markup-name-reviewer", Sw5eRoles.Contributor);

        var queue = await reviewer.GetAsync("/api/flags");
        var raw = await queue.Content.ReadAsStringAsync();

        raw.ShouldNotContain("<img");

        FlagFlow.FlagsIn(await queue.ReadJsonAsync())
            .ShouldHaveSingleItem()
            .GetProperty("reporter").GetProperty("displayName").GetString()
            .ShouldBe("<img src=x onerror=alert(1)>");
    }

    [Fact]
    public async Task TheSameAccountCannotFileTheSameReportTwice()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "duplicate");

        var first = await FlagFlow.RaiseAsync(
            client, "image-attribution-missing", FlagFlow.ImageType, FlagFlow.ImageKey);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await FlagFlow.RaiseAsync(
            client, "image-attribution-missing", FlagFlow.ImageType, FlagFlow.ImageKey);

        // A conflict rather than a silent success: answering 201 would tell the
        // reporter they had filed a second report, and would make a
        // double-click indistinguishable from a deliberate resubmission.
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("duplicate-report");

        (await FlagFlow.StoredAsync(_factory)).Count.ShouldBe(1);

        // A different reason against the same picture is a different report and
        // is not suppressed — the constraint is about repetition, not about
        // limiting how much one person may notice.
        var different = await FlagFlow.RaiseAsync(
            client, "image-replacement-wanted", FlagFlow.ImageType, FlagFlow.ImageKey);

        different.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TwoAccountsMayReportTheSamePicture()
    {
        // The suppression is per reporter, and it has to be: a hundred and
        // fifty people recognising the same uncredited portrait is the outcome
        // this feature was built to produce, not a flood to be deduplicated.
        var first = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, first, "crowd-one");

        var second = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, second, "crowd-two");

        (await FlagFlow.RaiseAsync(
            first, "image-artist-known", FlagFlow.ImageType, FlagFlow.ImageKey, "By A."))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await FlagFlow.RaiseAsync(
            second, "image-artist-known", FlagFlow.ImageType, FlagFlow.ImageKey, "By A. as well."))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await FlagFlow.StoredAsync(_factory)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task AReporterSeesTheirOwnReportsAndNobodyElsesAndNoReviewerNotes()
    {
        var mine = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, mine, "mine");
        var flagId = await FlagFlow.RaiseAcceptedAsync(
            mine, "text-error", details: "Missing full stop.");

        var theirs = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, theirs, "theirs");
        await FlagFlow.RaiseAcceptedAsync(
            theirs, "content-missing", details: "The level 14 feature is absent.");

        var reviewer = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewer, "mine-reviewer", Sw5eRoles.Contributor);

        var acted = await reviewer.PutAsJsonAsync(
            $"/api/flags/{flagId}/status",
            new { status = "declined", note = "Internal: the punctuation is house style." });

        acted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var own = await mine.GetAsync("/api/flags/mine");
        own.StatusCode.ShouldBe(HttpStatusCode.OK);

        var flags = FlagFlow.FlagsIn(await own.ReadJsonAsync());

        flags.Length.ShouldBe(1);
        flags[0].GetProperty("id").GetGuid().ShouldBe(flagId);
        flags[0].GetProperty("status").GetString().ShouldBe("declined");

        // Closing the loop matters — a report filed into a void is a report
        // nobody files twice — but the triage note is written between the
        // people working the queue and is not part of it.
        flags[0].GetProperty("reviewerNote").ValueKind.ShouldBe(JsonValueKind.Null);
        (await own.Content.ReadAsStringAsync()).ShouldNotContain("house style");
    }

    [Fact]
    public async Task AnAnonymousCallerCannotReadAnybodysReports()
    {
        var client = _factory.CreateBrowserClient();

        (await client.GetAsync("/api/flags/mine")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }
}
