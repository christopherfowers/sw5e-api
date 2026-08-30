using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Pins the exact JSON the browser application is written against.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this folder is about behaviour: that a bad signature is
/// refused, that a lockout counts, that an enrolment window closes. This one is
/// about vocabulary, and it exists because behaviour being right is not the
/// same as two codebases agreeing.
/// </para>
/// <para>
/// The browser client lives in a separate repository and was written from a
/// written specification rather than from this service. Both sides had full
/// test suites and both were green, and they still disagreed about the envelope
/// on <c>/me</c>, the spelling of the two-factor literal, the name of the
/// passkey label field, the capitalisation of every role, and the content type
/// of an error. Nothing caught any of it, because each side was tested against
/// its own idea of the other. A suite that only ever asserts
/// <c>response.IsSuccessStatusCode</c> would have been just as green.
/// </para>
/// <para>
/// So these tests name literals. They assert that the property is called
/// <c>twoFactorEnabled</c> and not <c>mfa</c>, that the status string is
/// <c>mfaRequired</c> and not <c>mfa-required</c>, that the role is
/// <c>Community</c> and not <c>community</c>. That looks pedantic and reads
/// badly, and it is the entire point: renaming any of them is a breaking change
/// for a client this repository cannot see, and a test that spells the name out
/// is the only thing standing between such a rename and a silent outage.
/// </para>
/// <para>
/// The reconciled contract these assertions encode is written up in
/// <c>docs/account-api-contract.md</c>, which is committed to both
/// repositories. If an assertion here changes, that document and the browser
/// client change with it.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AccountWireContractTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// The property names on the account projection, which the account area
    /// reads directly.
    /// </summary>
    [Fact]
    public async Task CurrentUserCarriesExactlyTheAgreedProperties()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-me");

        await account.EstablishAsync(_factory.Email);

        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);

        // No envelope. The account object is the whole body; a client reading
        // body.user would get undefined.
        var body = await me.ReadJsonAsync();
        body.ValueKind.ShouldBe(JsonValueKind.Object);

        PropertyNamesOf(body).ShouldBe(
            ["id", "email", "displayName", "roles", "twoFactorEnabled", "passkeys"],
            ignoreOrder: true);

        // Spelled out rather than derived: the flag is a flat boolean called
        // twoFactorEnabled, not a nested { mfa: { totp } } object.
        body.GetProperty("twoFactorEnabled").ValueKind.ShouldBe(JsonValueKind.False);

        // A list, not a count. Revocation needs to name a credential.
        body.GetProperty("passkeys").ValueKind.ShouldBe(JsonValueKind.Array);
        body.GetProperty("passkeys").GetArrayLength().ShouldBe(1);

        var passkey = body.GetProperty("passkeys")[0];
        PropertyNamesOf(passkey).ShouldBe(["id", "name", "createdAt"], ignoreOrder: true);
        passkey.GetProperty("id").GetString().ShouldNotBeNullOrWhiteSpace();
        passkey.GetProperty("name").GetString().ShouldBe("Test device");

        // There is no lastUsedAt and there must not be one: nothing records it,
        // so a client that renders it would be rendering a fiction.
        passkey.TryGetProperty("lastUsedAt", out _).ShouldBeFalse();
    }

    /// <summary>
    /// The role names, exactly as the browser client must compare them.
    /// </summary>
    /// <remarks>
    /// Capitalised, and the highest privilege is spelled <c>Administrator</c>
    /// rather than <c>admin</c>. A client comparing against lowercase strings
    /// discards every role the server sent and silently treats an administrator
    /// as an ordinary reader — which is exactly what the browser client did
    /// before this was written down.
    /// </remarks>
    [Fact]
    public async Task RolesUseTheirCapitalisedNames()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-roles");

        await account.EstablishAsync(_factory.Email);

        var body = await (await client.GetAsync("/api/auth/me")).ReadJsonAsync();

        var roles = body.GetProperty("roles").EnumerateArray()
            .Select(role => role.GetString())
            .ToArray();

        roles.ShouldBe(["Community"]);
    }

    /// <summary>Registration's single non-committal answer.</summary>
    [Fact]
    public async Task RegistrationAnswersPendingWithAMessage()
    {
        var client = _factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = AccountFlow.NewAddress("wire-register"), displayName = "Wire Test" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = await response.ReadJsonAsync();
        PropertyNamesOf(body).ShouldBe(["status", "message"], ignoreOrder: true);
        body.GetProperty("status").GetString().ShouldBe("pending");
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verification hands back an enrolment window rather than a session.
    /// </summary>
    /// <remarks>
    /// This is the shape the browser client was most wrong about: it expected a
    /// <c>user</c> here and a session with it, on the reasoning that otherwise a
    /// new account could never enrol its first passkey. The reasoning was sound
    /// and the conclusion was wrong — the ticket cookie is what makes enrolment
    /// reachable — so the test asserts both halves: the body carries
    /// <c>enrollmentExpiresAt</c> and no account detail, and <c>/me</c> is still
    /// 401 immediately afterwards.
    /// </remarks>
    [Fact]
    public async Task VerificationOpensAnEnrolmentWindowAndDoesNotSignAnybodyIn()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-verify");

        await account.RegisterAsync();

        var response = await client.PostAsJsonAsync(
            "/api/auth/email/verify",
            new { email = account.EmailAddress, token = _factory.Email.LatestToken(account.EmailAddress) });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();
        PropertyNamesOf(body).ShouldBe(["status", "enrollmentExpiresAt"], ignoreOrder: true);
        body.GetProperty("status").GetString().ShouldBe("verified");
        body.GetProperty("enrollmentExpiresAt").GetDateTimeOffset()
            .ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // No session was established. The ticket authorises enrolment and
        // nothing else.
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And enrolment really is reachable on the strength of the ticket
        // alone, which is the property that makes the flow finishable at all.
        var begin = await client.PostAsync("/api/auth/passkey/register/begin", content: null);
        begin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Both WebAuthn options documents arrive unwrapped.
    /// </summary>
    /// <remarks>
    /// The browser client expected a <c>publicKey</c> envelope around each,
    /// because that is the shape <c>navigator.credentials</c> takes as an
    /// argument. The server sends what
    /// <c>PublicKeyCredential.parseCreationOptionsFromJSON()</c> parses, which
    /// is the inner document; the client is the one that wraps it.
    /// </remarks>
    [Fact]
    public async Task WebAuthnOptionsAreNotWrappedInAPublicKeyEnvelope()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-options");

        await account.RegisterAsync();
        await account.VerifyEmailAsync(_factory.Email);

        var creation = await (await client.PostAsync(
            "/api/auth/passkey/register/begin", content: null)).ReadJsonAsync();

        creation.TryGetProperty("publicKey", out _).ShouldBeFalse();
        creation.TryGetProperty("challenge", out _).ShouldBeTrue();
        creation.TryGetProperty("rp", out _).ShouldBeTrue();
        creation.TryGetProperty("user", out _).ShouldBeTrue();
        creation.TryGetProperty("pubKeyCredParams", out _).ShouldBeTrue();

        var request = await (await client.PostAsync(
            "/api/auth/passkey/login/begin", content: null)).ReadJsonAsync();

        request.TryGetProperty("publicKey", out _).ShouldBeFalse();
        request.TryGetProperty("challenge", out _).ShouldBeTrue();
        request.TryGetProperty("rpId", out _).ShouldBeTrue();

        // Named no credentials, for every caller alike. A client that offered
        // an email field here would be promising a filter the server does not
        // apply.
        request.GetProperty("allowCredentials").GetArrayLength().ShouldBe(0);
    }

    /// <summary>The enrolment confirmation, whose label field is `name`.</summary>
    [Fact]
    public async Task EnrolmentConfirmationNamesTheCredentialAndItsLabel()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-enrol");

        await account.RegisterAsync();
        await account.VerifyEmailAsync(_factory.Email);

        var begin = await client.PostAsync("/api/auth/passkey/register/begin", content: null);
        var credential = account.Authenticator.Create(await begin.Content.ReadAsStringAsync());

        var complete = await client.PostAsJsonAsync(
            "/api/auth/passkey/register/complete",
            new { credential, name = "Work laptop" });

        complete.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await complete.ReadJsonAsync();
        PropertyNamesOf(body).ShouldBe(["credentialId", "name", "createdAt"], ignoreOrder: true);
        body.GetProperty("credentialId").GetString().ShouldNotBeNullOrWhiteSpace();

        // The label the client sent under `name` came back. A client sending
        // `label` would enrol every credential nameless and never be told.
        body.GetProperty("name").GetString().ShouldBe("Work laptop");
    }

    /// <summary>Sign-in's two outcomes, spelled exactly.</summary>
    [Fact]
    public async Task SignInStatusesAreAuthenticatedAndMfaRequired()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-signin");

        await account.RegisterAsync();
        await account.VerifyEmailAsync(_factory.Email);
        await account.EnrollPasskeyAsync();

        var signIn = await account.SignInAsync();
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await signIn.ReadJsonAsync();
        body.GetProperty("status").GetString().ShouldBe("authenticated");
        body.GetProperty("user").GetProperty("email").GetString().ShouldBe(account.EmailAddress);

        // Now switch on a second factor and sign in again, to pin the other
        // literal. Note the spelling: camelCase, no hyphen.
        var enrol = await (await client.PostAsync(
            "/api/auth/mfa/totp/enroll", content: null)).ReadJsonAsync();

        var secret = TimeBasedOneTimePassword.SecretFrom(
            enrol.GetProperty("authenticatorUri").GetString()!);

        await client.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        await client.PostAsync("/api/auth/logout", content: null);

        var challenged = await account.SignInAsync();
        challenged.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pending = await challenged.ReadJsonAsync();
        pending.GetProperty("status").GetString().ShouldBe("mfaRequired");

        // The challenge carries no account detail at all. One factor is not
        // permission to read anything.
        pending.GetProperty("user").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// Two-factor enrolment's shape, including the recovery codes the browser
    /// client believed were not returned.
    /// </summary>
    [Fact]
    public async Task TwoFactorEnrolmentReturnsEnabledWithRecoveryCodes()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-totp");

        await account.EstablishAsync(_factory.Email);

        var enrol = await client.PostAsync("/api/auth/mfa/totp/enroll", content: null);
        var enrolBody = await enrol.ReadJsonAsync();

        PropertyNamesOf(enrolBody).ShouldBe(["sharedKey", "authenticatorUri"], ignoreOrder: true);

        var secret = TimeBasedOneTimePassword.SecretFrom(
            enrolBody.GetProperty("authenticatorUri").GetString()!);

        var verify = await client.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await verify.ReadJsonAsync();
        PropertyNamesOf(body).ShouldBe(["status", "recoveryCodes"], ignoreOrder: true);

        // "enabled", not "enrolled".
        body.GetProperty("status").GetString().ShouldBe("enabled");
        body.GetProperty("recoveryCodes").GetArrayLength().ShouldBe(10);
    }

    /// <summary>
    /// Every refusal is a problem document, including the ones raised before a
    /// handler runs.
    /// </summary>
    /// <remarks>
    /// The content type matters as much as the body. It is
    /// <c>application/problem+json</c>, which does not contain the substring
    /// <c>application/json</c> — a client checking for that substring classifies
    /// every error as "the service is not there", which is what the browser
    /// client did. The message field is <c>detail</c>.
    /// </remarks>
    [Fact]
    public async Task RefusalsAreProblemDocumentsWithADetail()
    {
        var client = _factory.CreateBrowserClient();

        var invalid = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = "not-an-address", displayName = "Wire Test" });

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalid.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var body = await invalid.ReadJsonAsync();
        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("status").GetInt32().ShouldBe(400);

        // The anonymous 401 is raised by the authentication handler rather than
        // by the endpoint, and it too must carry a body. A bodiless 401 is
        // indistinguishable from a proxy answering while the API is unmounted,
        // and a client that guessed wrong would tell every signed-out reader
        // the service was down instead of offering them a way in.
        var anonymous = await client.GetAsync("/api/auth/me");

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await anonymous.ReadJsonAsync()).GetProperty("detail").GetString()
            .ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The emailed link points at a route the site actually serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion that would have caught the worst defect of the
    /// set. The link used to be built against <c>/account/verify</c>, which the
    /// browser application does not serve — it prerenders a fixed list of paths
    /// and answers anything else with its not-found page — so every
    /// verification and recovery message this service sent led nowhere and no
    /// account created through the front door could ever be finished.
    /// </para>
    /// <para>
    /// Nor could it have lived under <c>/account</c>: everything below that path
    /// is behind the site's session guard, and this link exists precisely for
    /// somebody who has no session yet.
    /// </para>
    /// <para>
    /// The path is spelled out rather than read from configuration, because the
    /// thing under test is agreement with another repository's route table, and
    /// a test that read the value from the same constant the code reads would
    /// agree with itself no matter what either side said.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEmailedLinkAddressesTheSitesVerificationRoute()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "wire-link");

        await account.RegisterAsync();

        var message = _factory.Email.For(account.EmailAddress)
            .Single(candidate => candidate.Kind == AccountMessageKind.Verification);

        var link = new Uri(message.Body);

        link.AbsolutePath.ShouldBe("/verify-email");

        // Both parameters, because the verify endpoint pairs the token with the
        // address it was issued for and refuses a link that lost either.
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        query["email"].ShouldBe(account.EmailAddress);
        query["token"].ShouldNotBeNullOrWhiteSpace();
    }

    private static string[] PropertyNamesOf(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];
}
