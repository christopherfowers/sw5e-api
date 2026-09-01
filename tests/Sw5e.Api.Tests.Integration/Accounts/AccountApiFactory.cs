using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sw5e.Identity.Email;
using Sw5e.Infrastructure.Persistence.Moderation;

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

    /// <summary>Requests allowed per window to have a sign-in code emailed.</summary>
    /// <remarks>
    /// Same reasoning as above, and separate because the production value is a
    /// tenth of the sensitive one — a suite that shared the number would be
    /// throttled by the tests that are not about throttling.
    /// </remarks>
    protected virtual int EmailCodeRequests => 1000;

    /// <summary>
    /// The clock every part of the identity stack reads.
    /// </summary>
    /// <remarks>
    /// Substituted so that a test about a code expiring can move the clock
    /// forward instead of sleeping. Sleeping for eleven real minutes is not a
    /// test anybody will keep, which in practice means expiry goes untested,
    /// which means the one property that bounds how long a stolen code is worth
    /// stealing is the one nothing checks.
    /// </remarks>
    public AdjustableTimeProvider Clock { get; } = new();

    /// <summary>
    /// Whether the real mail adapter is replaced by the recording one.
    /// </summary>
    /// <remarks>
    /// True for every test that needs to read a link or a code out of a
    /// message, which is nearly all of them. The one suite that turns it off is
    /// the one whose subject is what the API does when delivery fails: that
    /// behaviour lives in ProviderAccountEmailSender, and a fixture that
    /// replaced the adapter would be testing a stand-in instead of it. That
    /// suite substitutes the provider underneath the adapter instead.
    /// </remarks>
    protected virtual bool RecordEmailInsteadOfSending => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Content:RootPath", ContentApiFactory.FixturePath);
        builder.UseSetting("ConnectionStrings:Sw5eIdentity", postgres.ConnectionString);

        // The moderation schema shares the test container, in a schema of its
        // own with a migration history of its own — exactly the arrangement a
        // single-database deployment gets. Named explicitly rather than left to
        // the identity fallback so this fixture states which database the flag
        // endpoints write to instead of inheriting it.
        builder.UseSetting("ConnectionStrings:Sw5eModeration", postgres.ConnectionString);

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

        builder.UseSetting(
            "Auth:RateLimits:EmailCodeRequests",
            EmailCodeRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureServices(services =>
        {
            if (RecordEmailInsteadOfSending)
            {
                services.RemoveAll<IAccountEmailSender>();
                services.AddSingleton<IAccountEmailSender>(Email);
            }

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>
    /// Applies the moderation migration before the first request, the way the
    /// deploy-time job does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identity brings its own schema up at startup here, behind a setting that
    /// is off everywhere else. Moderation deliberately has no such setting: the
    /// rule that a web process must never migrate its own database is one this
    /// feature had no reason to weaken, and adding a second switch would have
    /// made it easier for the next feature to weaken it too.
    /// </para>
    /// <para>
    /// So the fixture plays the migrator's part instead, and it does so by
    /// calling the migrator's own method rather than a reimplementation of it.
    /// A test that reproduced the migration in its own code would prove those
    /// steps work; it would prove nothing about the code the deployment runs,
    /// which is the thing that can be broken.
    /// </para>
    /// <para>
    /// It runs after the host has been built and started and before any request
    /// is made, which is the same ordering a deployment has — the job finishes,
    /// then traffic arrives.
    /// </para>
    /// </remarks>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        ModerationServiceCollectionExtensions
            .MigrateModerationAsync(
                host.Services,
                host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sw5e.Tests"))
            .GetAwaiter()
            .GetResult();

        return host;
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

    public Task SendSignInCodeAsync(
        AccountEmailRecipient recipient,
        string code,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(new AccountMessage(AccountMessageKind.SignInCode, recipient.EmailAddress, code));
        return Task.CompletedTask;
    }

    public Task SendUnknownAddressSignInNoticeAsync(
        string emailAddress, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(new AccountMessage(
            AccountMessageKind.UnknownAddressNotice, emailAddress, string.Empty));

        return Task.CompletedTask;
    }

    /// <summary>
    /// The most recent sign-in code emailed to an address.
    /// </summary>
    /// <remarks>
    /// The tests read the code out of the captured message for the same reason
    /// they read verification tokens out of captured links: a test that
    /// generated its own code would be asserting that the server accepts codes
    /// the test made up, which is a property no correct server has.
    /// </remarks>
    public string LatestSignInCode(string emailAddress) =>
        _messages
            .Where(message =>
                message.Kind == AccountMessageKind.SignInCode &&
                message.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Body)
            .LastOrDefault()
        ?? throw new InvalidOperationException($"No sign-in code was emailed to {emailAddress}.");

    /// <summary>How many messages of one kind an address has been sent.</summary>
    public int CountOf(AccountMessageKind kind, string emailAddress) =>
        _messages.Count(message =>
            message.Kind == kind &&
            message.EmailAddress.Equals(emailAddress, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Carries a live credential in its body, and nothing else does.</summary>
    SignInCode,

    /// <summary>
    /// Sent to an address that asked for a sign-in code and has no account.
    /// </summary>
    /// <remarks>
    /// Its existence is the mechanism, not a nicety: sending on both branches
    /// is what keeps the request endpoint from answering an unregistered
    /// address measurably faster than a registered one.
    /// </remarks>
    UnknownAddressNotice,
}

public sealed record AccountMessage(AccountMessageKind Kind, string EmailAddress, string Body);

/// <summary>
/// A clock the tests move by hand.
/// </summary>
/// <remarks>
/// Starts at the real current time rather than at an epoch, because the
/// identity stack's own cookies and data protection payloads are stamped
/// against the real clock and a fixture that started in 1970 would produce a
/// session that had expired before it was issued. Only the offset is under the
/// test's control.
/// </remarks>
public sealed class AdjustableTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() =>
        System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>Moves the clock forward. Nothing here ever moves it back.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}
