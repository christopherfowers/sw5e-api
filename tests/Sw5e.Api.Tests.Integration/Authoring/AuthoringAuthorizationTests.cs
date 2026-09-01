using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Api.Tests.Integration.Moderation;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// Who may change published content, and who may not.
/// </summary>
/// <remarks>
/// <para>
/// These are the endpoints that can rewrite canonical rules for the whole
/// community, so every refusal is asserted twice: the status code, and a direct
/// read of the database proving that nothing was written. A handler that
/// refuses and writes anyway passes the first assertion on its own.
/// </para>
/// <para>
/// Each test here fails if the guard it names is removed. Dropping
/// <c>RequireAuthorization</c> from the draft route turns the anonymous and
/// Community cases into 204s; dropping <c>StrongAuthenticationRequirement</c>
/// from <c>sw5e:contribute</c> turns the emailed-code case into a 204;
/// weakening the publish route from <c>sw5e:administer</c> to
/// <c>sw5e:contribute</c> turns the contributor-publish case into a 200; and
/// removing the group's <c>CrossSiteRequestFilter</c> turns the cross-site case
/// into a 204.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AuthoringAuthorizationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAnonymousCallerCannotSaveADraft()
    {
        var client = _factory.CreateBrowserClient();
        var key = AuthoringFlow.NewKey("anon");

        var response = await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Valid(key, "Anonymous", "Should never be stored."));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The refusal has to have prevented the write, not merely reported one.
        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredItemAsync(_factory, key)).ShouldBeNull();
    }

    [Fact]
    public async Task AnAnonymousCallerCannotReadTheWorklist()
    {
        var client = _factory.CreateBrowserClient();

        (await client.GetAsync("/api/authoring/drafts")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACommunityAccountCannotSaveADraft()
    {
        var client = _factory.CreateBrowserClient();

        // A signed-in account with no elevated role. Passkey sign-in, so the
        // session is strongly authenticated: what is missing here is the role
        // and nothing else, which is what makes this test about the role.
        await FlagFlow.SignInAsync(_factory, client, "community-author");

        var key = AuthoringFlow.NewKey("community");

        var response = await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Valid(key, "Community", "Should never be stored."));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredItemAsync(_factory, key)).ShouldBeNull();
    }

    [Fact]
    public async Task AContributorWhoSignedInWithAnEmailedCodeCannotSaveADraft()
    {
        var client = _factory.CreateBrowserClient();

        var contributor = await FlagFlow.SignInWithRoleAsync(
            _factory, client, "weak-author", Sw5eRoles.Contributor);

        var control = AuthoringFlow.NewKey("strong");

        // The control. The same account, with a passkey session, is allowed —
        // so the refusal below is about the second factor and not about the
        // account, the role or the request.
        (await AuthoringFlow.SaveDraftAsync(
                client, control, AuthoringFlow.Valid(control, "Strong", "Stored.")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await client.PostAsync("/api/auth/logout", content: null);

        // Sign the same account back in with an emailed one-time code. That is
        // a real session — /api/auth/me answers — but the code is not a second
        // factor, so it must not carry the contributor's authoring privileges.
        var weak = _factory.CreateBrowserClient();

        await weak.PostAsJsonAsync("/api/auth/email/code", new { email = contributor.EmailAddress });

        var signIn = await weak.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new
            {
                email = contributor.EmailAddress,
                code = _factory.Email.LatestSignInCode(contributor.EmailAddress),
            });

        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await weak.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var key = AuthoringFlow.NewKey("weak");

        var refused = await AuthoringFlow.SaveDraftAsync(
            weak, key, AuthoringFlow.Valid(key, "Weak", "Should never be stored."));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await refused.ReadJsonAsync()).GetProperty("code").GetString()
            .ShouldBe("strong-authentication-required");

        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredItemAsync(_factory, key)).ShouldBeNull();
    }

    [Fact]
    public async Task AContributorCannotPublishTheirOwnDraft()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, "contrib-publish", Sw5eRoles.Contributor);

        var key = AuthoringFlow.NewKey("contrib");

        // Drafting is allowed.
        (await AuthoringFlow.SaveDraftAsync(
                client, key, AuthoringFlow.Valid(key, "Contributor Draft", "Drafted.")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Publishing is not. This is what makes the review step real rather
        // than conventional: without it, drafting and publishing are the same
        // act and the draft state buys nothing.
        var refused = await AuthoringFlow.PublishAsync(client, key);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await AuthoringFlow.StoredItemAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredRevisionsAsync(_factory, key)).ShouldBeEmpty();

        // The draft survived the refusal: a rejected publish must not destroy
        // the work it declined to publish.
        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AnAdministratorPublishesTheDraftAContributorWrote()
    {
        var contributorClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, contributorClient, "handoff-contrib", Sw5eRoles.Contributor);

        var key = AuthoringFlow.NewKey("handoff");

        (await AuthoringFlow.SaveDraftAsync(
                contributorClient, key, AuthoringFlow.Valid(key, "Handed Off", "Written by one, published by another.")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var adminClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, adminClient, "handoff-admin", Sw5eRoles.Administrator);

        var published = await AuthoringFlow.PublishAsync(adminClient, key, "Checked against the book.");

        published.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await AuthoringFlow.StoredItemAsync(_factory, key);
        stored.ShouldNotBeNull();
        stored.Name.ShouldBe("Handed Off");

        // The draft is consumed by publication rather than left behind to be
        // published a second time.
        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldBeNull();
    }

    [Fact]
    public async Task AWriteWithoutAnOriginIsRefused()
    {
        // One client, one session, one role. The Origin header is dropped
        // partway through, so the only difference between the request that is
        // allowed and the one that is refused is the header a browser always
        // sends and a cross-site form cannot forge. For a cookie-authenticated
        // API that is the CSRF defence.
        //
        // It has to be the same authenticated client rather than a second,
        // originless one: authorization runs ahead of the endpoint filter, so
        // an unauthenticated caller is refused with 401 before the cross-site
        // check is ever reached, and a test written that way would pass with
        // the filter deleted.
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, "csrf-author", Sw5eRoles.Contributor);

        var control = AuthoringFlow.NewKey("csrfok");

        (await AuthoringFlow.SaveDraftAsync(
                client, control, AuthoringFlow.Valid(control, "Same Site", "Stored.")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        client.DefaultRequestHeaders.Remove("Origin");

        var key = AuthoringFlow.NewKey("csrf");

        var response = await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Valid(key, "Cross Site", "Should never be stored."));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldBeNull();
        (await AuthoringFlow.StoredItemAsync(_factory, key)).ShouldBeNull();
    }

    [Fact]
    public async Task ACommunityAccountCannotReadAnothersUnpublishedWork()
    {
        var contributorClient = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, contributorClient, "private-contrib", Sw5eRoles.Contributor);

        var key = AuthoringFlow.NewKey("private");

        await AuthoringFlow.SaveDraftAsync(
            contributorClient, key, AuthoringFlow.Valid(key, "Unpublished", "Not for readers yet."));

        var reader = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, reader, "private-reader");

        (await reader.GetAsync($"/api/authoring/drafts/{AuthoringFlow.Type}/{key}")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        (await reader.GetAsync("/api/authoring/drafts")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        // And the draft has not leaked into the public catalogue either, which
        // is the property the separate draft table exists to guarantee.
        (await reader.GetAsync($"/api/content/{AuthoringFlow.Type}/{key}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }
}
