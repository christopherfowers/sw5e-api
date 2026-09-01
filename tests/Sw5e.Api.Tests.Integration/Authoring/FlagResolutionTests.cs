using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Api.Tests.Integration.Moderation;
using Sw5e.Domain.Moderation;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// Accepting a report and fixing it stop being the same button.
/// </summary>
/// <remarks>
/// Before this, a reviewer could agree with a report and then had nowhere to
/// go: the queue could record that somebody agreed, and nothing else. These
/// tests walk the whole loop — a reader reports a mistake, a reviewer accepts
/// it, a contributor drafts the correction naming the report, an administrator
/// publishes, and the report closes pointing at the revision that closed it.
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class FlagResolutionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();
        await FlagFlow.ClearAsync(_factory);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task PublishingTheFixResolvesTheReportAndNamesTheRevision()
    {
        var adminClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, adminClient, "loop-admin", Sw5eRoles.Administrator);

        // Something to report a problem with. The catalogue is served from the
        // database in this host, so the document has to be published before it
        // can be flagged.
        var key = AuthoringFlow.NewKey("loop");

        await AuthoringFlow.SaveDraftAsync(
            adminClient, key, AuthoringFlow.Valid(key, "Mispelled Name", "Wording with a mistake in it."));
        (await AuthoringFlow.PublishAsync(adminClient, key)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A reader notices.
        var readerClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, readerClient, "loop-reader");

        var flagId = await FlagFlow.RaiseAcceptedAsync(
            readerClient,
            "text-error",
            AuthoringFlow.Type,
            key,
            "The name is spelled wrong.");

        // A reviewer agrees. This is where the trail used to stop.
        var reviewerClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, reviewerClient, "loop-reviewer", Sw5eRoles.Contributor);

        (await reviewerClient.PutAsJsonAsync(
                $"/api/flags/{flagId}/status",
                new { status = "accepted", note = "Confirmed against the book." }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The fix, drafted against the report it answers.
        (await AuthoringFlow.SaveDraftAsync(
                reviewerClient,
                key,
                AuthoringFlow.Valid(key, "Misspelled Name", "Wording with a mistake in it."),
                flagId))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var published = await AuthoringFlow.PublishAsync(
            adminClient, key, "Corrected the spelling reported in the queue.");

        published.StatusCode.ShouldBe(HttpStatusCode.OK);

        var revisionId = (await published.ReadJsonAsync()).GetProperty("id").GetInt64();

        var flag = await FlagFlow.StoredAsync(_factory, flagId);

        flag.Status.ShouldBe(FlagStatus.Resolved);
        flag.ResolvedByRevisionId.ShouldBe(revisionId);

        // And the correction is what readers get.
        var served = await adminClient.GetAsync($"/api/content/{AuthoringFlow.Type}/{key}");
        (await served.ReadJsonAsync()).GetProperty("name").GetString().ShouldBe("Misspelled Name");
    }

    /// <summary>
    /// Publishing does not overturn a report nobody has triaged.
    /// </summary>
    /// <remarks>
    /// An open report has not been looked at, and a declined one was somebody's
    /// decision. Neither should be closed as a side effect of publishing
    /// something that happens to name it — that would let a draft silently
    /// dispose of a report a reviewer had not agreed with.
    /// </remarks>
    [Fact]
    public async Task PublishingDoesNotCloseAReportNobodyHasAccepted()
    {
        var adminClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, adminClient, "untriaged-admin", Sw5eRoles.Administrator);

        var key = AuthoringFlow.NewKey("untriaged");

        await AuthoringFlow.SaveDraftAsync(
            adminClient, key, AuthoringFlow.Valid(key, "Original", "The original wording."));
        (await AuthoringFlow.PublishAsync(adminClient, key)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var readerClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, readerClient, "untriaged-reader");

        var flagId = await FlagFlow.RaiseAcceptedAsync(
            readerClient, "text-error", AuthoringFlow.Type, key, "I think this is wrong.");

        // Straight to a fix, with nobody having accepted the report.
        await AuthoringFlow.SaveDraftAsync(
            adminClient, key, AuthoringFlow.Valid(key, "Changed", "Different wording."), flagId);

        (await AuthoringFlow.PublishAsync(adminClient, key)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var flag = await FlagFlow.StoredAsync(_factory, flagId);

        flag.Status.ShouldBe(FlagStatus.Open);
        flag.ResolvedByRevisionId.ShouldBeNull();
    }
}
