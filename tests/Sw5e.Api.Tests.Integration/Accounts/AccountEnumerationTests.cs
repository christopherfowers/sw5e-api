using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// That an unauthenticated caller cannot learn whether an address has an
/// account here.
/// </summary>
/// <remarks>
/// The assertions compare whole responses — status line and body — rather than
/// spot-checking a field, because enumeration leaks through whatever differs.
/// A different status code, a different wording, an extra field: any of them
/// answers the attacker's question.
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AccountEnumerationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task RegisteringAnAddressThatAlreadyExistsIsIndistinguishableFromANewOne()
    {
        var client = _factory.CreateBrowserClient();
        var taken = AccountFlow.NewAddress("taken");
        var free = AccountFlow.NewAddress("free");

        // Establish the first account fully, so the second attempt hits the
        // most informative branch there is: a verified, credentialled account.
        var established = new AccountFlow(client, taken, "Taken");
        await established.EstablishAsync(_factory.Email);
        await client.PostAsync("/api/auth/logout", content: null);

        var stranger = _factory.CreateBrowserClient();

        var existing = await stranger.PostAsJsonAsync(
            "/api/auth/register", new { email = taken, displayName = "Somebody Else" });

        var brandNew = await stranger.PostAsJsonAsync(
            "/api/auth/register", new { email = free, displayName = "Somebody Else" });

        existing.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        brandNew.StatusCode.ShouldBe(existing.StatusCode);

        var existingBody = await existing.Content.ReadAsStringAsync();
        var newBody = await brandNew.Content.ReadAsStringAsync();

        existingBody.ShouldBe(newBody);
    }

    [Fact]
    public async Task RegisteringAgainstAnExistingAccountCannotRenameIt()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "rename");

        await account.EstablishAsync(_factory.Email);

        var before = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        before.GetProperty("displayName").GetString().ShouldBe("Test rename");

        // A stranger who knows the address submits a registration carrying a
        // display name of their choosing. Honouring it would let anybody rewrite
        // how an account is presented across the site without proving anything.
        var stranger = _factory.CreateBrowserClient();
        await stranger.PostAsJsonAsync(
            "/api/auth/register",
            new { email = account.EmailAddress, displayName = "Impersonator" });

        var after = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();
        after.GetProperty("displayName").GetString().ShouldBe("Test rename");
    }

    [Fact]
    public async Task VerifyingAnUnknownAddressIsIndistinguishableFromABadToken()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "verify-enum");

        await account.RegisterAsync();

        var unknown = await client.PostAsJsonAsync(
            "/api/auth/email/verify",
            new { email = AccountFlow.NewAddress("nobody"), token = "not-a-real-token" });

        var wrongToken = await client.PostAsJsonAsync(
            "/api/auth/email/verify",
            new { email = account.EmailAddress, token = "not-a-real-token" });

        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        wrongToken.StatusCode.ShouldBe(unknown.StatusCode);

        (await WithoutCorrelationAsync(wrongToken))
            .ShouldBe(await WithoutCorrelationAsync(unknown));
    }

    /// <summary>
    /// A response body with the per-request correlation identifier removed.
    /// </summary>
    /// <remarks>
    /// Problem Details stamps a distinct <c>traceId</c> onto every response, so
    /// two bodies are never byte-identical and comparing them raw would fail
    /// against a perfectly indistinguishable pair. The trace id is not an
    /// enumeration channel — it is freshly generated per request and says
    /// nothing about the account — but every other field is, so everything else
    /// is compared exactly.
    /// </remarks>
    private static async Task<string> WithoutCorrelationAsync(HttpResponseMessage response)
    {
        var body = await response.ReadJsonAsync();

        return string.Join(
            '\n',
            body.EnumerateObject()
                .Where(property => property.Name is not "traceId")
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value}"));
    }

    [Fact]
    public async Task ARegistrationAttemptAgainstAVerifiedAccountEmailsItsOwnerARecoveryLink()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "recovery-notice");

        await account.EstablishAsync(_factory.Email);

        var stranger = _factory.CreateBrowserClient();
        await stranger.PostAsJsonAsync(
            "/api/auth/register",
            new { email = account.EmailAddress, displayName = "Probe" });

        // The probe learns nothing from the response. The account holder learns
        // that somebody tried, which turns a silent reconnaissance step into a
        // notification they can act on.
        _factory.Email.For(account.EmailAddress)
            .ShouldContain(message => message.Kind == AccountMessageKind.Recovery);
    }

    [Fact]
    public async Task AMalformedAddressIsRejectedOutright()
    {
        var client = _factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new { email = "not-an-address", displayName = "Nobody" });

        // Refusing malformed input reveals nothing about any account — the
        // input never named one. Being specific here is what keeps the
        // deliberately vague answers elsewhere from looking like bugs.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _factory.Email.Messages.ShouldBeEmpty();
    }
}
