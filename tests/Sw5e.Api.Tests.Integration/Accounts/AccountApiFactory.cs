using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sw5e.Identity.Email;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// Hosts the API against the test container's PostgreSQL instance, with the
/// mail provider replaced by one that records what it was asked to send.
/// </summary>
/// <remarks>
/// <para>
/// Only two things are substituted, and both for the same reason: they are the
/// parts of the flow that leave the process. Mail cannot be delivered from a
/// test, and the browser's authenticator cannot be driven from one. Everything
/// else — the identity store, the migration, the cookie policy, the passkey
/// verifier, the rate limiter, the authorization policies — is exactly what
/// runs in production.
/// </para>
/// <para>
/// In particular the token in a verification email is the real token, generated
/// by the real provider and read back out of the captured message rather than
/// minted by the test. A test that generated its own token would be asserting
/// that the server accepts tokens the server made, which is true of a server
/// that accepts anything.
/// </para>
/// </remarks>
public class AccountApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    /// <summary>The origin the browser application is pretended to be served from.</summary>
    public const string Origin = "https://localhost";

    /// <summary>Everything the API tried to email, in order.</summary>
    public RecordingEmailSender Email { get; } = new();

    /// <summary>Attempts allowed per window on the credential-guessing endpoints.</summary>
    /// <remarks>
    /// Generous by default so the suite is not throttled by its own volume, and
    /// overridden by the one test whose subject is the limiter.
    /// </remarks>
    protected virtual int SensitiveAttempts => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Content:RootPath", ContentApiFactory.FixturePath);
        builder.UseSetting("ConnectionStrings:Sw5eIdentity", postgres.ConnectionString);

        // Applies the committed migration and seeds the roles before the first
        // request. In a deployment this is a separate step; here it is what
        // puts the schema under test.
        builder.UseSetting("Identity:InitializeDatabaseAtStartup", "true");

        // The relying party the passkeys are bound to. Matches the host the
        // test client addresses, because a mismatch is exactly the failure the
        // production configuration guards against.
        builder.UseSetting("Identity:RelyingPartyId", "localhost");
        builder.UseSetting("Identity:PublicSiteUrl", "https://sw5e.test");

        builder.UseSetting(
            "Auth:RateLimits:SensitiveAttempts",
            SensitiveAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAccountEmailSender>();
            services.AddSingleton<IAccountEmailSender>(Email);
        });
    }

    /// <summary>
    /// A client that behaves like the browser application: it addresses the API
    /// over HTTPS, so cookies marked <c>Secure</c> are actually stored and
    /// resent, and it sends the <c>Origin</c> header a browser would.
    /// </summary>
    /// <remarks>
    /// The HTTPS base address matters more than it looks. The session cookie is
    /// emitted with <c>Secure</c> in every environment; a client addressing
    /// <c>http://localhost</c> would be handed that cookie, decline to store
    /// it, and every authenticated test would fail for a reason unrelated to
    /// what it was testing.
    /// </remarks>
    public HttpClient CreateBrowserClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        client.DefaultRequestHeaders.Add("Origin", Origin);

        return client;
    }

    /// <summary>
    /// A client with no <c>Origin</c> header, for the tests that check the
    /// cross-site request defence.
    /// </summary>
    public HttpClient CreateOriginlessClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
}

/// <summary>
/// Records account email instead of sending it, and hands the tests the links
/// a real recipient would have clicked.
/// </summary>
public sealed class RecordingEmailSender : IAccountEmailSender
{
    private readonly ConcurrentQueue<AccountMessage> _messages = new();

    public IReadOnlyCollection<AccountMessage> Messages => [.. _messages];

    public Task SendEmailVerificationAsync(
        AccountEmailRecipient recipient, string verificationUrl, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(new AccountMessage(AccountMessageKind.Verification, recipient.EmailAddress, verificationUrl));
        return Task.CompletedTask;
    }

    public Task SendPasskeyRecoveryAsync(
        AccountEmailRecipient recipient, string recoveryUrl, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(new AccountMessage(AccountMessageKind.Recovery, recipient.EmailAddress, recoveryUrl));
        return Task.CompletedTask;
    }

    public Task SendSecurityNoticeAsync(
        AccountEmailRecipient recipient, string summary, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(new AccountMessage(AccountMessageKind.SecurityNotice, recipient.EmailAddress, summary));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The most recent verification or recovery link sent to an address, with
    /// its token pulled out of the query string exactly as the browser
    /// application would.
    /// </summary>
    public string LatestToken(string emailAddress)
    {
        var message = _messages
            .Where(candidate =>
                candidate.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase) &&
                candidate.Kind is AccountMessageKind.Verification or AccountMessageKind.Recovery)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No link was emailed to {emailAddress}.");

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(message.Body).Query);

        return query["token"]
            ?? throw new InvalidOperationException("The emailed link carried no token.");
    }

    public IReadOnlyList<AccountMessage> For(string emailAddress) =>
        [.. _messages.Where(message =>
            message.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase))];
}

public enum AccountMessageKind
{
    Verification,
    Recovery,
    SecurityNotice,
}

public sealed record AccountMessage(AccountMessageKind Kind, string EmailAddress, string Body);
