using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Api.Tests.Integration.Moderation;
using Sw5e.Domain.Content;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// What happens to the corpus and its history when content is written.
/// </summary>
[Collection(AccountTestCollection.Name)]
public sealed class AuthoringLifecycleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// A document that fails its schema is refused, and nothing at all is
    /// stored.
    /// </summary>
    /// <remarks>
    /// The schema is the real published one for <c>armor-property</c>:
    /// <c>description</c> is required, and <c>additionalProperties</c> is false.
    /// The document sent here omits the first and adds an extra property, so it
    /// violates the schema twice over in the two ways schemas are usually
    /// violated.
    /// <para>
    /// Removing the validator call from the authoring store turns this into a
    /// 204 with a stored draft — which is exactly the silent corpus degradation
    /// the check exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADocumentThatViolatesItsSchemaIsRefusedAndStoresNothing()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, "schema-author", Sw5eRoles.Contributor);

        var key = AuthoringFlow.NewKey("badschema");

        var response = await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.SchemaViolating(key));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.ReadJsonAsync();
        body.GetProperty("code").GetString().ShouldBe("schema-violation");

        // The failing assertions are reported, not swallowed: an author who is
        // told only "invalid" guesses, and the guess costs a reviewer's time.
        var errors = body.GetProperty("schemaErrors").EnumerateArray()
                         .Select(error => error.GetString() ?? string.Empty)
                         .ToArray();

        errors.ShouldNotBeEmpty();

        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredItemAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredRevisionsAsync(_factory, key)).ShouldBeEmpty();
    }

    /// <summary>
    /// A valid document published, then a second version published over it,
    /// then the first put back.
    /// </summary>
    /// <remarks>
    /// The revert is asserted on the catalogue row itself rather than on the
    /// response, because restoring the history without restoring the content is
    /// precisely the bug this test exists to catch.
    /// </remarks>
    [Fact]
    public async Task ARevertRestoresThePreviousContent()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, client, "revert-admin", Sw5eRoles.Administrator);

        var key = AuthoringFlow.NewKey("revert");

        await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Valid(key, "First Name", "The original wording."));
        (await AuthoringFlow.PublishAsync(client, key, "First publication."))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Valid(key, "Second Name", "Rewritten, and wrongly."));
        (await AuthoringFlow.PublishAsync(client, key, "A change that turns out to be wrong."))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterSecond = await AuthoringFlow.StoredItemAsync(_factory, key);
        afterSecond.ShouldNotBeNull();
        afterSecond.Name.ShouldBe("Second Name");
        afterSecond.Body.ShouldContain("Rewritten, and wrongly.");

        var history = await AuthoringFlow.StoredRevisionsAsync(_factory, key);
        history.Count.ShouldBe(2);
        history[0].Number.ShouldBe(1);
        history[1].Number.ShouldBe(2);

        var reverted = await AuthoringFlow.RevertAsync(
            client, key, history[0].Id, "Putting the original wording back.");

        reverted.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The content is genuinely back.
        var afterRevert = await AuthoringFlow.StoredItemAsync(_factory, key);
        afterRevert.ShouldNotBeNull();
        afterRevert.Name.ShouldBe("First Name");
        afterRevert.Body.ShouldContain("The original wording.");
        afterRevert.Body.ShouldNotContain("Rewritten, and wrongly.");

        // And the read path serves it, which is the thing a reader would see.
        var served = await client.GetAsync($"/api/content/{AuthoringFlow.Type}/{key}");
        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await served.ReadJsonAsync()).GetProperty("name").GetString().ShouldBe("First Name");

        // Undoing a change did not erase the fact that it was made. This is
        // the append-only property: three revisions, not one.
        var afterHistory = await AuthoringFlow.StoredRevisionsAsync(_factory, key);
        afterHistory.Count.ShouldBe(3);
        afterHistory[2].Action.ShouldBe(ContentAuthoringWire.From(ContentRevisionAction.Reverted));
        afterHistory[2].RevertedFromId.ShouldBe(history[0].Id);

        // The wrong version is still readable, so an auditor can see what was
        // undone rather than only that something was.
        afterHistory[1].Body.ShouldContain("Rewritten, and wrongly.");
    }

    /// <summary>
    /// A revision names the account that actually made the change.
    /// </summary>
    /// <remarks>
    /// Two different accounts publish two different changes, and each revision
    /// is checked against the identity store's own id for the account that made
    /// it. A store that recorded a constant, the first actor, or the wrong one
    /// of the two would pass a single-actor test and fails this one.
    /// </remarks>
    [Fact]
    public async Task ARevisionRecordsTheAccountThatMadeTheChange()
    {
        var firstClient = _factory.CreateBrowserClient();
        var first = await FlagFlow.SignInWithRoleAsync(
            _factory, firstClient, "actor-one", Sw5eRoles.Administrator);

        var secondClient = _factory.CreateBrowserClient();
        var second = await FlagFlow.SignInWithRoleAsync(
            _factory, secondClient, "actor-two", Sw5eRoles.Administrator);

        var firstId = await UserIdAsync(first.EmailAddress);
        var secondId = await UserIdAsync(second.EmailAddress);

        firstId.ShouldNotBe(secondId);

        var key = AuthoringFlow.NewKey("actors");

        await AuthoringFlow.SaveDraftAsync(
            firstClient, key, AuthoringFlow.Valid(key, "Written By One", "The first wording."));
        (await AuthoringFlow.PublishAsync(firstClient, key, "By the first account."))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await AuthoringFlow.SaveDraftAsync(
            secondClient, key, AuthoringFlow.Valid(key, "Edited By Two", "The second wording."));
        (await AuthoringFlow.PublishAsync(secondClient, key, "By the second account."))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var history = await AuthoringFlow.StoredRevisionsAsync(_factory, key);

        history.Count.ShouldBe(2);

        history[0].ActorUserId.ShouldBe(firstId);
        history[0].Action.ShouldBe(ContentAuthoringWire.From(ContentRevisionAction.Created));
        history[0].Reason.ShouldBe("By the first account.");

        history[1].ActorUserId.ShouldBe(secondId);
        history[1].Action.ShouldBe(ContentAuthoringWire.From(ContentRevisionAction.Updated));
        history[1].Reason.ShouldBe("By the second account.");
    }

    /// <summary>
    /// Publishing over a document somebody else has since published is refused
    /// rather than silently winning.
    /// </summary>
    [Fact]
    public async Task ADraftWrittenAgainstAnOldVersionIsRefused()
    {
        var firstClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, firstClient, "stale-one", Sw5eRoles.Administrator);

        var key = AuthoringFlow.NewKey("stale");

        await AuthoringFlow.SaveDraftAsync(
            firstClient, key, AuthoringFlow.Valid(key, "Base", "The base version."));
        (await AuthoringFlow.PublishAsync(firstClient, key)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A draft started against the version that exists now.
        await AuthoringFlow.SaveDraftAsync(
            firstClient, key, AuthoringFlow.Valid(key, "Slow Edit", "Started before the race."));

        // Somebody else publishes in the meantime. Publishing consumes the
        // draft, so this second author writes their own.
        var secondClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, secondClient, "stale-two", Sw5eRoles.Administrator);

        // The first author's draft is still outstanding; the second author
        // reverting the document moves it on without touching that draft.
        var history = await AuthoringFlow.StoredRevisionsAsync(_factory, key);
        (await AuthoringFlow.RevertAsync(secondClient, key, history[0].Id, "Moving it on."))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await AuthoringFlow.PublishAsync(firstClient, key);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("draft-stale");

        // Nothing was overwritten, and the draft was not thrown away either.
        var stored = await AuthoringFlow.StoredItemAsync(_factory, key);
        stored.ShouldNotBeNull();
        stored.Body.ShouldNotContain("Started before the race.");
        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldNotBeNull();
    }

    private async Task<Guid> UserIdAsync(string emailAddress)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Sw5eUser>>();

        var user = await users.FindByEmailAsync(emailAddress)
            ?? throw new InvalidOperationException($"No account for {emailAddress}.");

        return user.Id;
    }
}
