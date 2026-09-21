using Sw5e.Api.Features.Accounts;

namespace Sw5e.Api.Features.Site;

/// <summary>
/// Facts about this deployment that the browser application cannot work out
/// for itself: which environment it is, and whether account email is getting
/// out.
/// </summary>
/// <remarks>
/// <para>
/// The web tier is a static image, rendered at build time and promoted between
/// environments unchanged, so it cannot know where it is running. It asks here
/// after hydration.
/// </para>
/// <para>
/// Two rules keep a "test environment" banner off the live site. This endpoint
/// reports production unless told otherwise, and the client draws nothing
/// unless it receives an explicit "not production", so a timeout or a malformed
/// body is read as production. A missing banner in QA is a one-variable fix; a
/// banner on the live site tells every reader the reference is disposable.
/// </para>
/// <para>
/// The delivery flag is one global boolean with no per-address dimension, and
/// that is a security property rather than a simplification. The account
/// endpoints answer identically whether or not an address is registered, and
/// "did mail to this address fail" would rebuild the oracle they avoid.
/// </para>
/// </remarks>
public static class SiteEnvironmentEndpoint
{
    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/site/environment", (
                  IWebHostEnvironment environment,
                  AccountEmailDeliveryMonitor mail,
                  HttpContext context) =>
              {
                  // A one-line body naming an environment is exactly what a
                  // shared cache would hand to the wrong deployment. Both
                  // values cost a field read, so there is nothing to save.
                  context.Response.Headers.CacheControl = "no-store";

                  return Results.Ok(Describe(
                      environment.EnvironmentName, mail.Current.Delivering));
              })
              .WithName("getSiteEnvironment")
              .WithTags("Site")
              .WithSummary("Which deployment this is.")
              .WithDescription(
                  "Answers whether this deployment is production, and whether account email is " +
                  "currently reaching the mail provider. The site is static HTML promoted " +
                  "unchanged between environments, so it cannot know which one it is running " +
                  "in and asks here after hydration. A deployment that has not been told its " +
                  "environment reports production, so missing configuration can never produce " +
                  "a test-environment banner on the live site. The delivery flag says whether " +
                  "mail is getting out at all, never whether a particular address or message " +
                  "failed, and never carries the provider's reply.")
              .Produces<SiteEnvironmentResponse>()
              // The identity stack installs a fallback policy that denies
              // anything not marked otherwise, and a banner only signed-in
              // readers could see would miss almost everyone using QA.
              .AllowAnonymous();

        return routes;
    }

    /// <summary>
    /// Turns a host environment name into the answer the site receives.
    /// </summary>
    /// <remarks>
    /// Separate from the route so the blank-name case can be tested; a test
    /// host substitutes a name of its own, so it cannot reach that path.
    /// <para>
    /// <see cref="IHostEnvironment.IsProduction"/> alone is not enough. An
    /// unset <c>ASPNETCORE_ENVIRONMENT</c> gives the host <c>Production</c>,
    /// but one set to an empty value (a blank compose entry, an unset shell
    /// variable, a template rendering an absent field) gives an empty name,
    /// which the framework does not consider production. That would report the
    /// live site as a test environment. Blank is therefore normalised to
    /// <c>Production</c> before the flag is derived, so the name and the flag
    /// can never disagree.
    /// </para>
    /// </remarks>
    /// <param name="environmentName">
    /// Null, empty and whitespace all mean "nobody said", which means production.
    /// </param>
    /// <param name="accountEmailDelivering">
    /// Defaults to true so that silence leaves the site saying what it said
    /// before this field existed, rather than reporting mail as broken on the
    /// strength of not knowing.
    /// </param>
    public static SiteEnvironmentResponse Describe(
        string? environmentName, bool accountEmailDelivering = true)
    {
        var name = string.IsNullOrWhiteSpace(environmentName)
            ? Environments.Production
            : environmentName;

        return new SiteEnvironmentResponse(
            name,
            string.Equals(name, Environments.Production, StringComparison.OrdinalIgnoreCase),
            accountEmailDelivering);
    }

    /// <summary>What deployment this is.</summary>
    /// <param name="Name">
    /// The host environment name, for operators and logs. The site branches on
    /// <paramref name="IsProduction"/> alone, so renaming an environment cannot
    /// change what a reader sees.
    /// </param>
    /// <param name="IsProduction">
    /// The site draws its test-environment banner only when this is explicitly
    /// false.
    /// </param>
    /// <param name="AccountEmailDelivering">
    /// The site stops telling people to check an inbox only when this is
    /// explicitly false. Carries no address, count, timestamp or provider
    /// reply; those belong on <c>/health/ready</c> and in the log.
    /// </param>
    public sealed record SiteEnvironmentResponse(
        string Name, bool IsProduction, bool AccountEmailDelivering);
}
