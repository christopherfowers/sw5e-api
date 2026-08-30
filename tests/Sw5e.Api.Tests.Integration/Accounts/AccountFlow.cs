using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Drives the account API the way the browser application will, so that a test
/// which needs a signed-in account can get one in a line.
/// </summary>
/// <remarks>
/// <para>
/// These helpers make no assertions about the behaviour under test. They throw
/// when a step they did not come to test fails, so that a broken prerequisite
/// surfaces as a loud setup failure rather than as a confusing assertion
/// several lines later — but every property a test is actually about is
/// asserted in the test itself.
/// </para>
/// <para>
/// The one thing they never do is take a shortcut through the service
/// container. Accounts are created by posting to the endpoints, verified with
/// the token from the captured email, and signed in with a real assertion from
/// the virtual authenticator, because a fixture that reached into the store to
/// fabricate a signed-in user would leave the actual sign-in path untested.
/// </para>
/// </remarks>
internal sealed class AccountFlow(HttpClient client, string emailAddress, string displayName)
{
    private static int _sequence;

    public VirtualAuthenticator Authenticator { get; } = new(AccountApiFactory.Origin);

    public string EmailAddress { get; } = emailAddress;

    /// <summary>
    /// An address nothing else in the suite will use. The tests share one
    /// database, so a fixed address would make them order-dependent.
    /// </summary>
    public static string NewAddress(string label) =>
        $"{label}-{Interlocked.Increment(ref _sequence)}-{Guid.NewGuid():N}@sw5e.test";

    public static AccountFlow For(HttpClient client, string label) =>
        new(client, NewAddress(label), $"Test {label}");

    /// <summary>Posts a registration and expects it to be accepted.</summary>
    public async Task RegisterAsync()
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = EmailAddress, displayName });

        Expect(response, HttpStatusCode.Accepted, "register");
    }

    /// <summary>
    /// Redeems the token from the emailed link, which opens the passkey
    /// enrolment window.
    /// </summary>
    public async Task VerifyEmailAsync(RecordingEmailSender email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/email/verify",
            new { email = EmailAddress, token = email.LatestToken(EmailAddress) });

        Expect(response, HttpStatusCode.OK, "email/verify");
    }

    /// <summary>Runs a full passkey enrolment ceremony.</summary>
    public async Task EnrollPasskeyAsync()
    {
        var begin = await client.PostAsync("/api/auth/passkey/register/begin", content: null);
        Expect(begin, HttpStatusCode.OK, "passkey/register/begin");

        var credential = Authenticator.Create(await begin.Content.ReadAsStringAsync());

        var complete = await client.PostAsJsonAsync(
            "/api/auth/passkey/register/complete",
            new { credential, name = "Test device" });

        Expect(complete, HttpStatusCode.Created, "passkey/register/complete");
    }

    /// <summary>
    /// Runs a full passkey sign-in ceremony and returns the response, without
    /// asserting anything about it — the caller decides whether a session or an
    /// MFA challenge was the right outcome.
    /// </summary>
    public async Task<HttpResponseMessage> SignInAsync(string? originOverride = null)
    {
        var begin = await client.PostAsync("/api/auth/passkey/login/begin", content: null);
        Expect(begin, HttpStatusCode.OK, "passkey/login/begin");

        var credential = Authenticator.Get(
            await begin.Content.ReadAsStringAsync(),
            originOverride: originOverride);

        return await client.PostAsJsonAsync(
            "/api/auth/passkey/login/complete",
            new { credential });
    }

    /// <summary>
    /// Registers, verifies, enrols a passkey and signs in — the complete
    /// journey from nothing to a session.
    /// </summary>
    public async Task<AccountFlow> EstablishAsync(RecordingEmailSender email)
    {
        await RegisterAsync();
        await VerifyEmailAsync(email);
        await EnrollPasskeyAsync();

        var signIn = await SignInAsync();
        Expect(signIn, HttpStatusCode.OK, "passkey/login/complete");

        return this;
    }

    private static void Expect(HttpResponseMessage response, HttpStatusCode expected, string step)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        throw new InvalidOperationException(
            $"Setup step '{step}' expected {(int)expected} but got {(int)response.StatusCode}: {body}");
    }
}

internal static class JsonResponseExtensions
{
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
