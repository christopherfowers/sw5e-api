namespace Sw5e.Api.Features.Health;

public static class HealthEndpoint
{
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

        return routes;
    }

    public sealed record HealthResponse(string Status);
}
