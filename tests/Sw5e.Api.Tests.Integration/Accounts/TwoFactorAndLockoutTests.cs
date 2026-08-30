using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Two-factor enrolment, the second factor at sign-in, and the lockout that
/// bounds guessing at it.
/// </summary>
[Collection(AccountTestCollection.Name)]
public sealed class TwoFactorAndLockoutTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task TheEnrolmentSecretWorksInAStandardAuthenticatorApp()
    {
        var client = _factory.CreateBrowserClient();
        await AccountFlow.For(client, "totp-secret").EstablishAsync(_factory.Email);

        var enrol = await client.PostAsync("/api/auth/mfa/totp/enroll", content: null);
        enrol.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await enrol.ReadJsonAsync();
        var uri = body.GetProperty("authenticatorUri").GetString()!;

        uri.ShouldStartWith("otpauth://totp/");
        uri.ShouldContain("issuer=SW5e");

        // The code is computed here from the URI's secret using an independent
        // RFC 6238 implementation — the same arithmetic a phone app performs.
        // The server accepting it is what proves the QR code it hands out is
        // actually usable, rather than merely well formed.
        var code = TimeBasedOneTimePassword.Generate(TimeBasedOneTimePassword.SecretFrom(uri));

        var verify = await client.PostAsJsonAsync("/api/auth/mfa/totp/verify", new { code });
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);

        var verified = await verify.ReadJsonAsync();
        verified.GetProperty("status").GetString().ShouldBe("enabled");
        verified.GetProperty("recoveryCodes").GetArrayLength().ShouldBe(10);

        // The manually-typed key and the QR code's secret must be the same
        // value, or somebody typing it in gets an account they cannot enter.
        var sharedKey = body.GetProperty("sharedKey").GetString()!;
        sharedKey.Replace(" ", string.Empty).ToUpperInvariant()
            .ShouldBe(TimeBasedOneTimePassword.SecretFrom(uri));
    }

    [Fact]
    public async Task APasskeyAloneDoesNotSignInAnAccountThatEnabledASecondFactor()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "mfa-gate");

        await account.EstablishAsync(_factory.Email);
        await EnableTwoFactorAsync(client);

        await client.PostAsync("/api/auth/logout", content: null);

        var signIn = await account.SignInAsync();
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await signIn.ReadJsonAsync();
        body.GetProperty("status").GetString().ShouldBe("mfaRequired");

        // The assertion above is not enough on its own — an endpoint could
        // report mfaRequired and have issued a session anyway. This is the
        // check that the second factor is actually load-bearing, and it is the
        // reason this API does not use SignInManager.PasskeySignInAsync, which
        // completes the sign-in with bypassTwoFactor set.
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Nor does the half-finished sign-in describe the account it belongs
        // to. Passing one factor is not yet permission to read anything, and
        // the address is the most useful thing an attacker holding a stolen
        // authenticator could be handed.
        (await signIn.Content.ReadAsStringAsync())
            .ShouldNotContain(account.EmailAddress, Case.Insensitive);
    }

    [Fact]
    public async Task TheCorrectCodeCompletesASignInThatNeededASecondFactor()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "mfa-complete");

        await account.EstablishAsync(_factory.Email);
        var secret = await EnableTwoFactorAsync(client);

        await client.PostAsync("/api/auth/logout", content: null);
        await account.SignInAsync();

        var verify = await client.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verify.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("authenticated");

        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.ReadJsonAsync()).GetProperty("twoFactorEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task RepeatedWrongCodesLockTheAccountEvenAgainstTheRightOne()
    {
        var client = _factory.CreateBrowserClient();
        var account = AccountFlow.For(client, "lockout");

        await account.EstablishAsync(_factory.Email);
        var secret = await EnableTwoFactorAsync(client);

        await client.PostAsync("/api/auth/logout", content: null);

        // The policy is five failures. Each attempt starts from a fresh passkey
        // assertion, so every one of them is a genuine second-factor failure
        // that reaches the account's counter — rather than four requests bouncing
        // off a pending-sign-in cookie that the first failure had already spent.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var signIn = await account.SignInAsync();
            signIn.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await signIn.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("mfaRequired");

            var wrong = await client.PostAsJsonAsync(
                "/api/auth/mfa/totp/verify", new { code = "000000" });

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            await client.PostAsync("/api/auth/logout", content: null);
        }

        // This is the assertion the whole test exists for. The passkey that
        // succeeded five times in the loop above is now refused outright:
        // guessing at the second factor has locked the account, rather than
        // merely failing each time. Without this, the test would pass against a
        // system that rejects wrong codes and has no lockout at all.
        var afterLockout = await account.SignInAsync();
        afterLockout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And the correct code cannot rescue it either.
        var correct = await client.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACodeFromNobodyWithAHalfFinishedSignInIsRefused()
    {
        var stranger = _factory.CreateBrowserClient();

        var response = await stranger.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify", new { code = "123456" });

        // No session and no pending sign-in: there is nothing for a code to
        // complete, and the endpoint must not treat an unknown caller as either
        // an enroller or a signer-in.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EnrolmentIsRefusedWithoutASession()
    {
        var response = await _factory.CreateBrowserClient()
            .PostAsync("/api/auth/mfa/totp/enroll", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Turns on two-factor authentication for the signed-in account and returns
    /// the secret, so the test can produce codes for it.
    /// </summary>
    private static async Task<string> EnableTwoFactorAsync(HttpClient client)
    {
        var enrol = await client.PostAsync("/api/auth/mfa/totp/enroll", content: null);
        var uri = (await enrol.ReadJsonAsync()).GetProperty("authenticatorUri").GetString()!;
        var secret = TimeBasedOneTimePassword.SecretFrom(uri);

        var verify = await client.PostAsJsonAsync(
            "/api/auth/mfa/totp/verify",
            new { code = TimeBasedOneTimePassword.Generate(secret) });

        if (verify.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Two-factor enrolment failed: {await verify.Content.ReadAsStringAsync()}");
        }

        return secret;
    }
}
