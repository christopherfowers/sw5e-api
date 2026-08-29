namespace Sw5e.Api.Features.Health;

public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")))
              .WithName("Health")
              .WithSummary("Liveness probe.")
              .AllowAnonymous();

        return routes;
    }

    public sealed record HealthResponse(string Status);
}
