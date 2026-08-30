using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Sw5e.Api.Features.Health;

public static class HealthEndpoint
{
    /// <summary>
    /// Tag marking the checks that decide readiness. Kept in step with
    /// <c>Sw5eDatabaseHealthCheck.ReadyTag</c>, which is where the database
    /// check applies it.
    /// </summary>
    private const string ReadyTag = "ready";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        // Two paths, one handler, and the second one is not redundant.
        //
        // The container's own HEALTHCHECK probes /health from inside, where
        // there is no proxy and the path is whatever this app says it is. From
        // outside, the QA reverse proxy routes /api/* to this service *without*
        // stripping the prefix, so everything reachable through it lives under
        // /api — and /api/health, the obvious thing for an external monitor to
        // watch, answered 404.
        //
        // Mapping both is better than moving the endpoint: moving it would
        // break the baked-in HEALTHCHECK and every existing probe, to fix a
        // path that costs one route entry to add.
        //
        // Liveness answers from nothing but the fact that the process is
        // running and routing works, and it must stay that way. A liveness
        // probe that consulted the database would fail during a database
        // outage, and an orchestrator would then restart every API container:
        // that does nothing for the database and destroys the capacity still
        // serving cached responses and the endpoints that need no database at
        // all. Readiness below is where a dependency belongs.
        foreach (var (path, name) in new[] { ("/health", "Health"), ("/api/health", "HealthThroughProxy") })
        {
            routes.MapGet(path, () => Results.Ok(new HealthResponse("healthy")))
                  .WithName(name)
                  .WithSummary("Liveness probe.")
                  .WithDescription(
                      "Answers 200 whenever the process is running and able to route. It never " +
                      "consults a dependency, so it stays a statement about this container only. " +
                      "Served at both /health, which the container image probes directly, and " +
                      "/api/health, which is where it lands through the reverse proxy.")
                  .AllowAnonymous();
        }

        // Readiness. Reports whether the dependencies this instance needs in
        // order to serve are actually usable, which for the database means
        // reachable and migrated up to what this build expects.
        //
        // Mapped on both paths for the same reason liveness is: an external
        // monitor reaches this service through the proxy, where everything
        // lives under /api, and a readiness probe nobody outside the host can
        // call is a readiness probe nobody watches.
        foreach (var (path, name) in new[] { ("/health/ready", "Readiness"), ("/api/health/ready", "ReadinessThroughProxy") })
        {
            routes.MapHealthChecks(path, new()
            {
                Predicate = registration => registration.Tags.Contains(ReadyTag),
                ResponseWriter = WriteReadinessAsync,
            })
            .WithName(name)
            .WithSummary("Readiness probe.")
            .AllowAnonymous();
        }

        return routes;
    }

    /// <summary>
    /// Writes the readiness report as JSON.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than left to the default writer, which emits the
    /// plain status string and nothing else. Naming the failing check is what
    /// makes the probe useful in a deploy log. What is deliberately not written
    /// is the exception: a connection failure from Npgsql names the host, port,
    /// database and user, and this endpoint is reachable by anything that can
    /// reach the API. The exception is still logged.
    /// </remarks>
    private static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsJsonAsync(new ReadinessResponse(
            Describe(report.Status),
            [.. report.Entries.Select(entry => new ReadinessCheck(
                entry.Key,
                Describe(entry.Value.Status),
                entry.Value.Description))]));
    }

    private static string Describe(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };

    public sealed record HealthResponse(string Status);

    /// <summary>The readiness report: an overall status and one entry per check.</summary>
    public sealed record ReadinessResponse(string Status, IReadOnlyList<ReadinessCheck> Checks);

    /// <summary>One dependency's verdict.</summary>
    /// <param name="Name">Check name, such as <c>database</c>.</param>
    /// <param name="Status">One of <c>healthy</c>, <c>degraded</c> or <c>unhealthy</c>.</param>
    /// <param name="Description">
    /// What the check has to say. Written by the check itself and safe to
    /// return; it never carries a connection string, a host name or a stack
    /// trace.
    /// </param>
    public sealed record ReadinessCheck(string Name, string Status, string? Description);
}
