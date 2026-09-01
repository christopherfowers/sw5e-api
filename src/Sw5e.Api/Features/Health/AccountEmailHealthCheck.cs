using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sw5e.Api.Features.Accounts;

namespace Sw5e.Api.Features.Health;

/// <summary>
/// Reports whether account mail is reaching the provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this belongs on the readiness surface at all.</b> A relay that refuses
/// everything takes registration, verification, sign-in codes and passkey
/// recovery with it: nobody new can get in and nobody locked out can get back
/// in, and none of those callers sees an error, because these endpoints answer
/// identically whether or not mail got out. Without a report here the first
/// symptom is a support message days later. Readiness is where this deployment
/// already states which of its dependencies are usable, and mail is one.
/// </para>
/// <para>
/// <b>Why it is never unhealthy.</b> The standing rule is that
/// <c>/health/ready</c> must not take the site out of rotation for reasons that
/// do not warrant it, and this is squarely one of those. Every replica sends
/// through the same relay, so failing the probe cannot route around the fault —
/// it removes capacity from a site whose reading, searching and browsing are
/// entirely unaffected, and an orchestrator draining every instance turns a mail
/// outage into a total one. Degraded says the same thing to a human without
/// asking the infrastructure to act on it, exactly as the database check does
/// for a schema that is one migration behind.
/// </para>
/// <para>
/// <b>What it deliberately does not say.</b> Not the provider's reply, and not
/// any address. This endpoint is anonymous, and the reply is text a relay wrote
/// about a specific envelope; the log is where it belongs. What is published
/// here is one global fact — mail is getting out, or it is not — which is the
/// same answer for every reader and so cannot be turned into a question about
/// anybody's account.
/// </para>
/// </remarks>
internal sealed class AccountEmailHealthCheck(AccountEmailDeliveryMonitor monitor) : IHealthCheck
{
    /// <summary>Name this check is registered under.</summary>
    public const string Name = "account-email";

    /// <summary>Tag identifying checks that gate readiness rather than liveness.</summary>
    /// <remarks>
    /// Kept in step with the tag the health endpoint filters on. Liveness must
    /// not consult this: a mail outage is not a reason to restart a container.
    /// </remarks>
    public const string ReadyTag = "ready";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = monitor.Current;

        if (status.Delivering)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Account email is being accepted by the provider."));
        }

        return Task.FromResult(new HealthCheckResult(
            HealthStatus.Degraded,
            $"The provider has refused {status.ConsecutiveFailures} account " +
            $"{(status.ConsecutiveFailures == 1 ? "message" : "messages")} since it last " +
            $"accepted one, most recently a {status.LastFailureKind?.ToString().ToLowerInvariant()} " +
            "failure. Verification links, sign-in codes and recovery links are not arriving. " +
            "The provider's own reply is in the application log."));
    }
}
