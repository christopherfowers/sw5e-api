using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// What deleting an account does to the content it authored.
/// </summary>
/// <remarks>
/// <para>
/// This lives beside the authoring tests rather than beside the account ones
/// because it needs the database content store, and the authoring store is
/// registered only alongside it. On a file-backed deployment there are no
/// drafts and no revisions, and the deletion endpoint says as much by reporting
/// a null draft count rather than a zero.
/// </para>
/// <para>
/// Two rules, and they point in opposite directions on purpose. A revision is
/// history and survives the deletion; a draft is unfinished work and blocks it.
/// The line between them is whether anybody else is entitled to see the thing:
/// a published revision has already changed what the community reads, and a
/// draft has not changed anything yet and is holding the only editing slot for
/// the entry it names.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AccountDeletionAndAuthorshipTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAccountWithAnOutstandingDraftCannotBeDeleted()
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "delete-drafting-admin");

        var authorClient = _factory.CreateBrowserClient();
        var author = await AdministrationFlow.SignInWithRoleAsync(
            _factory, authorClient, "delete-drafting", Sw5eRoles.Contributor);

        var authorId = await AdministrationFlow.IdOfAsync(_factory, author.EmailAddress);

        var key = AuthoringFlow.NewKey("stranded");

        (await AuthoringFlow.SaveDraftAsync(
                authorClient,
                key,
                AuthoringFlow.Valid(key, "Stranded", "Half-written and not published.")))
            .IsSuccessStatusCode.ShouldBeTrue();

        var refused = await admin.DeleteAsync($"/api/auth/admin/users/{authorId}");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var problem = await refused.ReadJsonAsync();

        problem.GetProperty("code").GetString().ShouldBe("drafts-outstanding");
        problem.GetProperty("draftCount").GetInt32().ShouldBe(1);

        // The refusal is real: the account is still there and so is the draft.
        // A 409 from an endpoint that had already deleted would be worse than
        // no check at all.
        (await AdministrationFlow.ExistsAsync(_factory, authorId)).ShouldBeTrue();
        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldNotBeNull();

        // And the administrator can see the count before trying, rather than
        // meeting it for the first time as a refusal.
        var detail = await admin.GetAsync($"/api/auth/admin/users/{authorId}");

        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await detail.ReadJsonAsync()).GetProperty("outstandingDrafts").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task DiscardingTheDraftClearsTheWay()
    {
        // The control. Without it, an implementation that refused every
        // deletion on a database-backed deployment would pass the test above.
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "delete-cleared-admin");

        var authorClient = _factory.CreateBrowserClient();
        var author = await AdministrationFlow.SignInWithRoleAsync(
            _factory, authorClient, "delete-cleared", Sw5eRoles.Contributor);

        var authorId = await AdministrationFlow.IdOfAsync(_factory, author.EmailAddress);

        var key = AuthoringFlow.NewKey("cleared");

        await AuthoringFlow.SaveDraftAsync(
            authorClient, key, AuthoringFlow.Valid(key, "Cleared", "Discarded before deletion."));

        (await admin.DeleteAsync($"/api/auth/admin/users/{authorId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await authorClient.DeleteAsync($"/api/authoring/drafts/{AuthoringFlow.Type}/{key}"))
            .IsSuccessStatusCode.ShouldBeTrue();

        (await admin.DeleteAsync($"/api/auth/admin/users/{authorId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AdministrationFlow.ExistsAsync(_factory, authorId)).ShouldBeFalse();
    }

    [Fact]
    public async Task APublishedRevisionKeepsItsAuthorAfterTheAccountIsDeleted()
    {
        // The decision this whole feature had to make, proved on the record it
        // matters most for. A history that can be edited by deleting an account
        // is not a history, and the revision table is append-only at the
        // database precisely so that the people who can rewrite canonical rules
        // cannot quietly unmake the record of having done it.
        // Two administrators, because a revision records the account that
        // published it and publishing is an administrator's act. The one whose
        // name is on the revision is the one that gets deleted; the other does
        // the deleting.
        var authorClient = _factory.CreateBrowserClient();
        var author = await AdministrationFlow.AdministratorAsync(
            _factory, authorClient, "delete-revision-author");

        var authorId = await AdministrationFlow.IdOfAsync(_factory, author.EmailAddress);

        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "delete-revision-remover");

        var key = AuthoringFlow.NewKey("authored");

        await AuthoringFlow.SaveDraftAsync(
            authorClient, key, AuthoringFlow.Valid(key, "Authored", "Written by somebody who left."));

        // Publishing clears the draft, so the account that wrote it has nothing
        // outstanding and the deletion below is refused for no other reason.
        (await AuthoringFlow.PublishAsync(authorClient, key, "Reviewed and published."))
            .IsSuccessStatusCode.ShouldBeTrue();

        (await admin.DeleteAsync($"/api/auth/admin/users/{authorId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AdministrationFlow.ExistsAsync(_factory, authorId)).ShouldBeFalse();

        // The revision is still there, still attributed to the identifier that
        // wrote it. Nothing was blanked and nothing was reassigned to the
        // administrator who pressed delete.
        var history = await admin.GetAsync(
            $"/api/authoring/content/{AuthoringFlow.Type}/{key}/revisions");

        history.StatusCode.ShouldBe(HttpStatusCode.OK);

        var revisions = (await history.ReadJsonAsync())
            .GetProperty("revisions").EnumerateArray().ToArray();

        revisions.ShouldNotBeEmpty();

        revisions.Select(revision => revision.GetProperty("actorUserId").GetGuid())
            .ShouldContain(authorId);

        // The published document is still readable, unchanged, by everybody.
        // Deleting its author is not a way to unpublish content.
        var published = await _factory.CreateBrowserClient()
            .GetAsync($"/api/content/{AuthoringFlow.Type}/{key}");

        published.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
