using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Sw5e.Api.Security;

/// <summary>
/// How hard anybody may push the flagging endpoints, and how much one account
/// may file.
/// </summary>
/// <remarks>
/// Two halves, and they defend against different attackers. The per-caller
/// windows below stop one machine flooding the endpoint; the per-account
/// quotas stop one account filing all day from a rotating address, which is
/// the version of this attack that a limiter keyed on IP cannot see at all.
/// Neither is sufficient alone, which is why both are here.
/// </remarks>
public sealed class FlagRateLimitOptions
{
    public const string SectionName = "Flags:RateLimits";

    /// <summary>Reports one caller may file per <see cref="SubmitWindow"/>.</summary>
    /// <remarks>
    /// Ten in ten minutes. A reader working through a page of species portraits
    /// they recognise is the legitimate burst this has to survive, and it is
    /// comfortably under. Anything sustained above it is not a person reading.
    /// </remarks>
    public int SubmitRequests { get; set; } = 10;

    /// <summary>The window the submission budget is measured over.</summary>
    public TimeSpan SubmitWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Reads of the queue one caller may make per <see cref="ReadWindow"/>.</summary>
    /// <remarks>
    /// Generous, because the people hitting it are moderators paging through a
    /// list and the endpoint costs one indexed query. It exists to bound the
    /// work an authenticated abuser can cause, not to slow anybody down.
    /// </remarks>
    public int ReadRequests { get; set; } = 120;

    /// <summary>The window the read budget is measured over.</summary>
    public TimeSpan ReadWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reports one account may file in a rolling day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half that survives an attacker who has an account and a
    /// thousand addresses to send from, and it is checked in the handler rather
    /// than by the limiter because the limiter cannot see who is asking — it
    /// partitions on the client address, which is exactly what such an attacker
    /// changes.
    /// </para>
    /// <para>
    /// Fifty is far more than a real contributor files in a day and small
    /// enough that a compromised account cannot bury the queue before anybody
    /// notices. It is a soft ceiling on abuse, not a rationing of goodwill.
    /// </para>
    /// </remarks>
    public int AccountReportsPerDay { get; set; } = 50;

    /// <summary>
    /// Reports one account may have outstanding at once.
    /// </summary>
    /// <remarks>
    /// The daily limit bounds the rate; this bounds the standing total, so an
    /// account cannot spend a fortnight quietly accumulating seven hundred
    /// unreviewed rows at fifty a day. Filing again is possible as soon as
    /// reviewers work through what is already there, which is the correct
    /// pressure: the queue is a shared resource and this is one person's share
    /// of it.
    /// </remarks>
    public int AccountOutstandingReports { get; set; } = 40;
}

/// <summary>Rate limiting for the flag endpoints.</summary>
/// <remarks>
/// <para>
/// Its own policies rather than a share of the account budgets. Two reasons.
/// A shared budget lets somebody exhaust everybody's ability to file a report
/// by hammering sign-in, and — the one that actually decides it — the numbers
/// wanted here are nothing like the numbers wanted there: filing a report is
/// not a guess that could pay off, so the limit exists to bound volume rather
/// than to make brute force impractical.
/// </para>
/// <para>
/// The registration composes with <c>AddSw5eAuthRateLimiting</c> rather than
/// replacing it: <c>AddRateLimiter</c> appends a configuration action, so both
/// sets of policies end up on one limiter. The rejection status and the
/// <c>Retry-After</c> header are set again here, identically, so that this
/// module does not silently depend on the other having run first.
/// </para>
/// </remarks>
internal static class FlagRateLimiting
{
    /// <summary>Applied to the one endpoint that writes a report.</summary>
    public const string SubmitPolicy = "sw5e-flag-submit";

    /// <summary>Applied to the endpoints that read the queue.</summary>
    public const string ReadPolicy = "sw5e-flag-read";

    public static IServiceCollection AddSw5eFlagRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FlagRateLimitOptions>(
            configuration.GetSection(FlagRateLimitOptions.SectionName));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.OnRejected = static (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
                }

                return ValueTask.CompletedTask;
            };

            AddPolicy(limiter, SubmitPolicy, options =>
                (options.SubmitRequests, options.SubmitWindow));

            AddPolicy(limiter, ReadPolicy, options =>
                (options.ReadRequests, options.ReadWindow));
        });

        return services;
    }

    private static void AddPolicy(
        RateLimiterOptions limiter,
        string name,
        Func<FlagRateLimitOptions, (int Permits, TimeSpan Window)> select) =>
        limiter.AddPolicy(name, context =>
        {
            var options = context.RequestServices
                .GetRequiredService<IOptions<FlagRateLimitOptions>>().Value;

            var (permits, window) = select(options);

            return RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context, name),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = window,

                    // No queue, for the same reason the account endpoints have
                    // none: holding a connection open on an abuser's behalf
                    // turns a rate limit into a resource-exhaustion assist.
                    QueueLimit = 0,
                });
        });

    /// <summary>
    /// One bucket per client address per endpoint.
    /// </summary>
    /// <remarks>
    /// The client address is read after <c>UseForwardedHeaders</c>, so behind a
    /// correctly configured proxy it is the caller's own. Keyed on the route
    /// pattern rather than the raw path, so a caller cannot mint fresh
    /// partitions with a query string, a case change or a different flag
    /// identifier in the path.
    /// </remarks>
    private static string PartitionKey(HttpContext context, string policy)
    {
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var route = context.GetEndpoint() is RouteEndpoint endpoint
            ? endpoint.RoutePattern.RawText ?? context.Request.Path.Value ?? string.Empty
            : context.Request.Path.Value ?? string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{policy}|{client}|{route}");
    }
}
