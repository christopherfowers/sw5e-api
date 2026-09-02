using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Sw5e.Api.Security;

/// <summary>
/// How hard a single client may push the account endpoints.
/// </summary>
/// <remarks>
/// Configurable so that a deployment behind a busy corporate NAT can loosen the
/// window without a rebuild, and so that the tests can tighten it far enough to
/// prove the limiter genuinely refuses traffic rather than merely being
/// registered.
/// </remarks>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "Auth:RateLimits";

    /// <summary>
    /// Attempts allowed per window against a credential-guessing endpoint:
    /// registration, email verification, sign-in completion and code
    /// verification.
    /// </summary>
    /// <remarks>
    /// Twenty attempts every five minutes is 240 an hour. Against a six-digit
    /// TOTP code that is a one-in-4 000 chance of a hit per hour of sustained
    /// abuse, and the account lockout closes long before that — the limiter's
    /// job is to stop the attempt reaching the lockout counter thousands of
    /// times a second, not to be the only thing standing there.
    /// </remarks>
    public int SensitiveAttempts { get; set; } = 20;

    /// <summary>The window the sensitive budget is measured over.</summary>
    public TimeSpan SensitiveWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Requests allowed per window against the remaining account endpoints —
    /// the WebAuthn ceremony openers, the proof-of-work challenge, the
    /// current-user probe, sign-out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These cost work but do not reward guessing, so the budget exists to cap
    /// resource consumption rather than to slow an attack down.
    /// </para>
    /// <para>
    /// "Challenge" is two different things on this API and this budget covers
    /// both, so it is worth naming them rather than leaving a reader to guess
    /// which was meant. The WebAuthn challenges from
    /// <c>passkey/register/begin</c> and <c>passkey/login/begin</c> are a
    /// replay defence inside a credential ceremony; the proof-of-work challenge
    /// from <c>GET /challenge</c> is an anti-abuse toll paid before an
    /// unrelated request. Neither is a credential and neither rewards a guess,
    /// which is why they share a budget at all.
    /// </para>
    /// </remarks>
    public int StandardRequests { get; set; } = 120;

    /// <summary>The window the standard budget is measured over.</summary>
    public TimeSpan StandardWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Requests allowed per window to have a sign-in code emailed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Far tighter than anything else here, because this is the only endpoint
    /// on the platform where one anonymous request causes a message to be
    /// delivered to a mailbox the caller chose. Left on the ordinary sensitive
    /// budget it would be a mail cannon: twenty messages every five minutes,
    /// per address, pointed at whoever the caller likes.
    /// </para>
    /// <para>
    /// Five in fifteen minutes is more than a real person needs — the flow
    /// takes one, and a resend takes a second — and it is measured per caller
    /// rather than per address, so it is the half of the defence that survives
    /// an attacker who spreads one request each across ten thousand addresses.
    /// The other half, the per-address budget, lives in the identity options
    /// and is what survives an attacker with ten thousand addresses of their
    /// own to send from.
    /// </para>
    /// </remarks>
    public int EmailCodeRequests { get; set; } = 5;

    /// <summary>The window the email-code budget is measured over.</summary>
    public TimeSpan EmailCodeWindow { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Rate limiting for the account endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Brute force is the attack every credential endpoint faces first, and it is
/// the one that does not need a bug to work. Lockout answers it per account;
/// this answers it per caller, which is the half lockout cannot reach — an
/// attacker spreading one attempt each across ten thousand accounts never trips
/// a single lockout counter.
/// </para>
/// <para>
/// Partitions are keyed on the client address <em>and</em> the endpoint. Two
/// reasons: a budget shared across every endpoint lets an attacker exhaust
/// somebody else's ability to register by hammering sign-in, and separate
/// budgets mean the numbers above can be tuned per endpoint later without
/// reshaping anything.
/// </para>
/// </remarks>
internal static class AuthRateLimiting
{
    /// <summary>Applied to endpoints where a guess could pay off.</summary>
    public const string SensitivePolicy = "sw5e-auth-sensitive";

    /// <summary>Applied to the remaining account endpoints.</summary>
    public const string StandardPolicy = "sw5e-auth-standard";

    /// <summary>
    /// Applied to the one endpoint that sends mail on an anonymous caller's
    /// say-so.
    /// </summary>
    public const string EmailCodePolicy = "sw5e-auth-email-code";

    public static IServiceCollection AddSw5eAuthRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AuthRateLimitOptions>(
            configuration.GetSection(AuthRateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            // 429 rather than the framework's default 503. A 503 says the
            // server is unwell; this server is fine and is refusing on purpose,
            // and a client that cannot tell the difference will retry the wrong
            // way.
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.OnRejected = static (context, cancellationToken) =>
            {
                // Retry-After turns a refusal into an instruction. Well-behaved
                // clients back off correctly instead of hot-looping, which is
                // the difference between a limiter that sheds load and one that
                // merely relabels it.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                return ValueTask.CompletedTask;
            };

            AddPolicy(limiter, SensitivePolicy, options =>
                (options.SensitiveAttempts, options.SensitiveWindow));

            AddPolicy(limiter, StandardPolicy, options =>
                (options.StandardRequests, options.StandardWindow));

            AddPolicy(limiter, EmailCodePolicy, options =>
                (options.EmailCodeRequests, options.EmailCodeWindow));
        });

        return services;
    }

    private static void AddPolicy(
        RateLimiterOptions limiter,
        string name,
        Func<AuthRateLimitOptions, (int Permits, TimeSpan Window)> select) =>
        limiter.AddPolicy(name, context =>
        {
            var options = context.RequestServices
                .GetRequiredService<IOptions<AuthRateLimitOptions>>().Value;

            var (permits, window) = select(options);

            return RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context, name),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = window,

                    // No queue. Queueing a credential attempt means holding a
                    // connection open on behalf of an attacker, which converts
                    // a rate limit into a resource-exhaustion assist. Refuse
                    // immediately instead.
                    QueueLimit = 0,
                });
        });

    private static string PartitionKey(HttpContext context, string policy)
    {
        // Read after UseForwardedHeaders has run, so behind a correctly
        // configured proxy this is the client's own address rather than the
        // proxy's. If the proxy trust list is wrong the value collapses to the
        // proxy address and every client shares one bucket — noisy, but it
        // fails towards refusing traffic rather than towards admitting it.
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // The route pattern rather than the raw path: /api/auth/... has no
        // route parameters today, but keying on the raw path would let a caller
        // mint unlimited fresh partitions with a query string or a case change
        // the moment one is added.
        var route = context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText ?? context.Request.Path.Value ?? string.Empty
            : context.Request.Path.Value ?? string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{policy}|{client}|{route}");
    }
}
