using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Api.Tests.Integration.Moderation;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// Publishing the shape of a content type.
/// </summary>
/// <remarks>
/// <para>
/// The point of this endpoint is that a client can generate an editor from the
/// same document the write path validates against, so the two cannot describe
/// different shapes. The test that matters most is therefore the last one: it
/// takes the schema the endpoint serves, builds a document that satisfies it,
/// and saves that document — proving the two agree rather than asserting they
/// look similar.
/// </para>
/// <para>
/// Each test here fails if the guard it names is removed. Dropping
/// <c>RequireAuthorization</c> turns the anonymous and Community cases into
/// 200s; dropping <c>StrongAuthenticationRequirement</c> from
/// <c>sw5e:contribute</c> turns the emailed-code case into a 200.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class ContentSchemaEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private const string Path = $"/api/authoring/schemas/{AuthoringFlow.Type}";

    [Fact]
    public async Task AnAnonymousCallerCannotReadASchema()
    {
        var client = _factory.CreateBrowserClient();

        (await client.GetAsync(Path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACommunityAccountCannotReadASchema()
    {
        // The schemas are public information — they live in a public
        // repository — so this is not protecting a secret. It keeps the
        // anonymous surface of a content-management API to the endpoints that
        // serve readers, and it costs the one caller that wants this nothing,
        // because that caller already holds the role.
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(_factory, client, "community-schema");

        (await client.GetAsync(Path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AContributorReadsTheSchemaWithItsVersionAndItsDescriptions()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, "schema-reader", Sw5eRoles.Contributor);

        var response = await client.GetAsync(Path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();

        // The canonical key, whatever the caller asked with.
        body.GetProperty("type").GetString().ShouldBe(AuthoringFlow.Type);
        body.GetProperty("version").GetInt32().ShouldBe(1);

        var schema = body.GetProperty("schema");
        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("required").EnumerateArray()
            .Select(entry => entry.GetString())
            .ShouldContain("description");

        // The descriptions are the reason this serves the file rather than a
        // re-serialised evaluator object: they are the only place the corpus
        // explains what a field means, and they are what an editor puts under
        // the control.
        schema.GetProperty("properties").GetProperty("description")
            .GetProperty("description").GetString()
            .ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheRouteSegmentResolvesToTheCanonicalKey()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, "schema-segment", Sw5eRoles.Contributor);

        var response = await client.GetAsync("/api/authoring/schemas/armor-properties");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Asked for with the plural segment, answered with the singular key.
        // A client that stored the answer under the name it asked with would
        // decide one content type was two.
        (await response.ReadJsonAsync()).GetProperty("type").GetString()
            .ShouldBe("armor-property");
    }

    [Fact]
    public async Task AnUnknownContentTypeIsNotFound()
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, "schema-unknown", Sw5eRoles.Contributor);

        (await client.GetAsync("/api/authoring/schemas/holocrons")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AContributorWhoSignedInWithAnEmailedCodeCannotReadASchema()
    {
        var client = _factory.CreateBrowserClient();

        var contributor = await FlagFlow.SignInWithRoleAsync(
            _factory, client, "weak-schema", Sw5eRoles.Contributor);

        // The control. The same account with a passkey session is allowed, so
        // the refusal below is about the second factor and nothing else.
        (await client.GetAsync(Path)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await client.PostAsync("/api/auth/logout", content: null);

        var weak = _factory.CreateBrowserClient();

        await weak.PostAsJsonAsync("/api/auth/email/code", new { email = contributor.EmailAddress });
        await weak.PostAsJsonAsync(
            "/api/auth/email/code/verify",
            new
            {
                email = contributor.EmailAddress,
                code = _factory.Email.LatestSignInCode(contributor.EmailAddress),
            });

        var refused = await weak.GetAsync(Path);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ReadJsonAsync()).GetProperty("code").GetString()
            .ShouldBe("strong-authentication-required");
    }

    [Fact]
    public async Task WhatTheSchemaDescribesIsWhatTheWritePathAccepts()
    {
        // The test this endpoint exists for. A schema served from one place and
        // evaluated from another would eventually describe a shape the write
        // path refuses, and the symptom would be an editor drawing a form that
        // cannot be saved. Building the document out of the served schema and
        // then saving it is the only assertion that rules that out.
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, client, "schema-agreement", Sw5eRoles.Contributor);

        var schema = (await (await client.GetAsync(Path)).ReadJsonAsync())
            .GetProperty("schema");

        var key = AuthoringFlow.NewKey("agree");
        var document = new Dictionary<string, object?>();

        foreach (var required in schema.GetProperty("required").EnumerateArray())
        {
            var name = required.GetString()!;
            var property = schema.GetProperty("properties").GetProperty(name);

            document[name] = name switch
            {
                "key" => key,
                // An enum is answered with one of its own values, read out of
                // the schema rather than written down here — so a schema that
                // changed its vocabulary would still produce a valid document.
                _ when property.TryGetProperty("enum", out var choices) =>
                    choices.EnumerateArray().First().GetString(),
                _ => "Written from the schema this endpoint served.",
            };
        }

        var saved = await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Parse(JsonSerializer.Serialize(document)));

        saved.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuthoringFlow.StoredDraftAsync(_factory, key)).ShouldNotBeNull();
    }
}
