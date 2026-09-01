using Sw5e.Api.Features.Accounts;

namespace Sw5e.Api.Features.Site;

/// <summary>
/// Tells the browser application the few facts about this deployment that it
/// cannot work out for itself.
/// </summary>
/// <remarks>
/// <para>
/// It began with one: the QA site has to say, visibly, that it is QA and that
/// nothing entered there is kept. It could not say so on its own. It now
/// carries a second — whether account email is getting out — for the same
/// reason and by the same route. See <em>Why the mail flag lives here</em>
/// below.
/// </para>
/// <para>
/// The web tier is a static nginx image serving HTML that was rendered at build
/// time. There is no server-side render at request time, so no environment
/// variable can be read per request on that side, and baking one in at build
/// time would mean QA and production are different images — which destroys the
/// only property that makes a promotion trustworthy, that the artifact which
/// passed QA is the artifact that reaches production, byte for byte. So the
/// question has to be answered by something that already differs between the
/// two deployments, at runtime, and that is this service: it has its own
/// configuration, its own database and its own mail provider in each
/// environment, and it therefore already knows the answer. The site asks it
/// after hydration and draws the banner from what comes back.
/// </para>
/// <para>
/// **Absence of configuration means production.** That is the whole safety
/// property, and it is stated here rather than left to be inferred, because a
/// "TEST ENVIRONMENT" banner appearing on the real site is a worse failure than
/// no banner in QA: the first tells every reader the reference they are using
/// is disposable, and the second is fixed by setting one variable in a place
/// where somebody is already looking. Two independent things have to go wrong
/// before a banner can reach production. First, this endpoint reports
/// production unless it is told otherwise: <c>ASPNETCORE_ENVIRONMENT</c> unset
/// gives <c>IWebHostEnvironment.EnvironmentName</c> the value
/// <c>Production</c>, which is the framework's own default, and a name that is
/// present but blank is normalised to the same thing by
/// <see cref="Describe"/> — see there for why that second case needs saying.
/// Second, the client draws nothing unless it receives an
/// explicit "not production" — an unreachable endpoint, a timeout, a proxy
/// answering with HTML during a partial deploy and a malformed body are all
/// treated as production. Silence is never read as QA.
/// </para>
/// <para>
/// The predicate is <see cref="IHostEnvironment.IsProduction"/> rather than a
/// list of names that count as test environments, and that is a deliberate
/// choice between two imperfect failure modes. An allow-list would mean a
/// deployment named something nobody thought of — <c>Preview</c>, <c>UAT</c> —
/// silently gets no banner, which is the exact failure the banner exists to
/// prevent and is invisible when it happens. The convention below inverts that:
/// a misspelled environment name in production shows a banner that should not
/// be there, which is wrong but is wrong loudly, on the first page anyone
/// loads, and is corrected in minutes. It is also the same predicate the rest
/// of this application already uses to decide HSTS and HTTPS redirection, so
/// the banner cannot end up disagreeing with the app about where it is running.
/// </para>
/// <para>
/// <b>Why the mail flag lives here.</b> A deployment's identity is a constant
/// and a relay's health is not, so putting the two in one document is worth
/// justifying rather than assuming. The case against is that they have
/// different lifetimes, and that a reader could reasonably expect a document
/// about "which environment this is" to be cacheable. That expectation is
/// already false: this response has been <c>no-store</c> since it was written,
/// because a one-line body naming an environment is exactly what a shared cache
/// would hold and later hand to the wrong deployment. So the lifetime objection
/// costs nothing here — there was never a cache to invalidate.
/// </para>
/// <para>
/// The case for is that a second anonymous endpoint is a second thing to route,
/// a second thing to leave unmounted during a partial deploy, a second thing to
/// name in a policy, and a second thing whose absence the client has to have an
/// opinion about — all of it to carry one boolean. The honest description of
/// this endpoint was never "the environment" but "what the prerendered site has
/// to ask, because it was built before the answer existed", and a relay
/// refusing everything is squarely that: the site cannot observe it, cannot
/// infer it, and is actively lying to people without it.
/// </para>
/// <para>
/// What the flag is <em>not</em> is an operations surface. It carries no count,
/// no timestamp, no permanent-versus-transient distinction and no provider
/// reply. Those stay on <c>/health/ready</c> and in the application log, which
/// have an audience already trusted with them. One boolean is published, it is
/// the same boolean for every caller, and it is exactly what the interface
/// needs in order to stop promising mail it already knows will not arrive.
/// </para>
/// <para>
/// <b>It is global, and that is the security property.</b> The account
/// endpoints answer 202 with an identical body whether or not an address has an
/// account, and nothing published here may weaken that. A per-address delivery
/// answer would rebuild the oracle precisely: asking whether mail to
/// <c>someone@example.com</c> failed is asking whether
/// <c>someone@example.com</c> has an account. What
/// <see cref="AccountEmailDeliveryMonitor"/> holds has no per-address dimension
/// at all, so there is no arrangement of requests that turns this field into an
/// answer about anybody. "Mail is not getting out" is true for every reader at
/// once and identifies none of them.
/// </para>
/// </remarks>
public static class SiteEnvironmentEndpoint
{
    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder routes)
    {
        // Under /api like everything else the browser calls, because the
        // deployment routes /api/* to this service without stripping the
        // prefix. That is also what keeps the site's Content-Security-Policy
        // at `connect-src 'self'`: the request is same-origin, so no host is
        // named in the policy and no CORS preflight is involved.
        routes.MapGet("/api/site/environment", (
                  IWebHostEnvironment environment,
                  AccountEmailDeliveryMonitor mail,
                  HttpContext context) =>
              {
                  // Never cached, anywhere. Neither answer is a property of the
                  // resource: one is a property of the deployment, the other of
                  // a relay in the last few minutes, and this is exactly the
                  // kind of one-line body a shared cache would happily hold and
                  // hand to the wrong environment — or, now, hand back long
                  // after the outage it described was over. Both cost a field
                  // read to compute, so there is nothing to save.
                  context.Response.Headers.CacheControl = "no-store";

                  // Read here rather than at the client's page load, and that
                  // ordering is the point: the account endpoints attempt their
                  // send before they answer, so a client that asks this after
                  // its 202 is reading a monitor that has already seen the
                  // failure its own request caused.
                  return Results.Ok(Describe(
                      environment.EnvironmentName, mail.Current.Delivering));
              })
              .WithName("getSiteEnvironment")
              .WithTags("Site")
              .WithSummary("Which deployment this is.")
              .WithDescription(
                  "Answers whether this deployment is production, and whether account email is " +
                  "currently reaching the mail provider. The site is served as static " +
                  "prerendered HTML from an image that is promoted unchanged between " +
                  "environments, so it cannot know which one it is running in and asks here " +
                  "after hydration. A deployment that has not been told its environment " +
                  "reports production, so the absence of configuration can never produce a " +
                  "test-environment banner on the live site. The delivery flag is one global " +
                  "fact with no per-address dimension: it says whether mail is getting out at " +
                  "all, never whether a particular message or a particular address failed, and " +
                  "it never carries the provider's own reply.")
              .Produces<SiteEnvironmentResponse>()
              // Explicitly anonymous. AddSw5eIdentity installs a fallback
              // authorization policy that denies anything which has not said
              // otherwise, and a banner that only appeared for signed-in
              // readers would miss almost everyone using QA.
              .AllowAnonymous();

        return routes;
    }

    /// <summary>
    /// Turns a host environment name into the answer the site receives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public and separate from the route because it is the safety property in
    /// one line, and a safety property that can only be exercised by standing up
    /// a web host is one nobody tests at its edges. The edge that matters is a
    /// name that is missing or blank, and that case cannot be reached through
    /// the test host at all: it substitutes a name of its own long before any of
    /// this runs.
    /// </para>
    /// <para>
    /// <see cref="IHostEnvironment.IsProduction"/> alone is not quite enough,
    /// and the gap is not theoretical. An <c>ASPNETCORE_ENVIRONMENT</c> that is
    /// never set gives the host the name <c>Production</c> and everything works.
    /// An <c>ASPNETCORE_ENVIRONMENT</c> that is set to nothing — an empty value
    /// in a compose file, a variable substituted from an unset shell variable, a
    /// deploy template rendering an absent field — gives the host an empty name,
    /// and an empty name is not production as far as the framework is concerned.
    /// Left alone, that reports the live site as a test environment, which is
    /// precisely the failure this whole endpoint is arranged to make impossible.
    /// </para>
    /// <para>
    /// So a blank name is normalised to <c>Production</c> rather than merely
    /// being answered as "not production", and the flag is then derived from the
    /// normalised name. An operator reading this body by hand can never see a
    /// name and a flag that disagree with each other.
    /// </para>
    /// </remarks>
    /// <param name="environmentName">
    /// <c>IWebHostEnvironment.EnvironmentName</c>. Null, empty and whitespace
    /// are all treated as "nobody said", which means production.
    /// </param>
    /// <param name="accountEmailDelivering">
    /// Whether account mail is currently reaching the provider, from
    /// <see cref="AccountEmailDeliveryMonitor"/>. Defaults to true, which is
    /// the same fail-safe direction as the environment flag but pointed the
    /// other way round, and for the same reason: the safe answer is the one
    /// that changes nothing. Silence here leaves the site saying exactly what
    /// it said before this field existed, rather than telling every reader that
    /// mail is broken on the strength of not knowing.
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
    /// The host environment name — <c>Production</c>, <c>QA</c>, <c>Development</c>.
    /// Returned for operators reading the response by hand and for a log line
    /// worth keeping; the site itself branches on <paramref name="IsProduction"/>
    /// alone, so renaming an environment can never change what a reader sees.
    /// </param>
    /// <param name="IsProduction">
    /// True for the live deployment. The site draws its test-environment banner
    /// when, and only when, this is explicitly false.
    /// </param>
    /// <param name="AccountEmailDelivering">
    /// True when nothing has failed to send recently. The site stops telling
    /// people to go and check an inbox when, and only when, this is explicitly
    /// false — an older service that does not send this field, an unreachable
    /// endpoint and a malformed body all leave the wording as it was.
    /// <para>
    /// One global boolean, and nothing else. No address appears in it, because
    /// the monitor behind it holds none; no provider reply appears in it,
    /// because that text can quote an envelope and this endpoint is anonymous;
    /// no count or timestamp appears in it, because nothing a reader can do
    /// changes with the number. Anything richer than this belongs on
    /// <c>/health/ready</c>, which exists for operators, or in the log.
    /// </para>
    /// </param>
    public sealed record SiteEnvironmentResponse(
        string Name, bool IsProduction, bool AccountEmailDelivering);
}
