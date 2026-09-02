using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sw5e.Api;
using Sw5e.Api.Features.Accounts;
using Sw5e.Api.Features.Content;
using Sw5e.Api.Features.Health;
using Sw5e.Api.Features.Moderation;
using Sw5e.Api.Features.Site;
using Sw5e.Api.Security;
using Sw5e.Domain.Content;
using Sw5e.Email.Configuration;
using Sw5e.Identity;
using Sw5e.Identity.Email;
using Sw5e.Infrastructure.Content;
using Sw5e.Infrastructure.Persistence;
using Sw5e.Infrastructure.Persistence.Moderation;

var builder = WebApplication.CreateBuilder(args);

// Suppress the server identity banner; it offers attackers free reconnaissance.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Behind a TLS-terminating proxy (Azure App Service, nginx, a load balancer)
// the connection Kestrel actually accepts is plain HTTP, so Request.Scheme is
// "http" and Request.IsHttps is false even though the client spoke HTTPS.
// Two things break silently as a result: UseHsts() emits nothing, because the
// HSTS middleware skips non-HTTPS requests, so the production HSTS policy
// never reaches a browser; and UseHttpsRedirection() issues a redirect the
// proxy forwards straight back as HTTP, producing a loop. The same scheme
// detection will later drive `Secure` cookie emission for identity, where
// getting it wrong means session cookies travel unprotected.
//
// Honouring X-Forwarded-Proto restores the original scheme. X-Forwarded-For
// is included so request logging and future rate limiting see the client
// address rather than the proxy's.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Forwarded headers are attacker-controllable: anything that can reach
    // Kestrel directly can claim any scheme and any source IP. ASP.NET Core
    // therefore trusts them from loopback only by default, and that default is
    // deliberately left in place here.
    //
    // A deployment whose proxy is NOT loopback (Azure App Service's front end
    // and any separate load balancer both qualify) MUST widen that trust
    // explicitly, or forwarded headers are ignored and the problems above
    // return. Configure it, for example via App Service application settings:
    //
    //   ForwardedHeaders__KnownProxies__0  = 10.0.0.4
    //   ForwardedHeaders__KnownNetworks__0 = 10.0.0.0/16
    //
    // Do not "fix" a misconfigured proxy by clearing KnownProxies and
    // KnownNetworks unless the app is genuinely unreachable except through
    // that proxy. An empty trust list makes the middleware accept forwarded
    // headers from every source, which hands any client the ability to spoof
    // its scheme and its address.
    foreach (var proxy in builder.Configuration
                 .GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }

    foreach (var network in builder.Configuration
                 .GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        // IPNetwork.Parse requires CIDR notation and rejects a base address
        // with host bits set, so a typo like 10.0.0.4/16 fails at startup
        // rather than silently widening or narrowing the trusted range.
        // Fully qualified: Microsoft.AspNetCore.HttpOverrides also defines an
        // IPNetwork, and the deprecated KnownNetworks property is the one that
        // takes it. KnownIPNetworks takes the System.Net type.
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

// One year, covering subdomains, and preload-eligible. The framework default
// is 30 days with neither flag, which is below the minimum every browser
// preload list requires and leaves subdomains unprotected.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
    options.Preload = true;
});

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Transactional email, behind IEmailSender so nothing above it learns which
// provider is configured. MailerSend is the intended production provider and a
// generic SMTP relay is the fallback; which one runs is Email:Provider and
// nothing else. See src/Sw5e.Email/README.md for the seam and for the contract
// the account flows consume.
//
// Registration validates eagerly and throws before the host is built, so a
// deployment missing its API token stops here rather than starting happily and
// silently dropping every password-reset email. Development is the one
// exception: with no provider configured it captures to the log, so the
// application runs with no credentials at all.
builder.Services.AddSw5eEmail(builder.Configuration, builder.Environment);

// Health checks are registered unconditionally so /health/ready exists in every
// configuration. With no store-specific checks added it reports healthy, which
// is the honest answer for a deployment whose only dependency is its own
// filesystem.
builder.Services.AddHealthChecks();

// The content store is registered behind IContentRepository so the endpoints
// never learn where the catalogue actually lives. Both implementations satisfy
// the same contract, and which one is in use is this one setting:
//
//   Content__Store=database   PostgreSQL, populated by the migrator
//   Content__Store=file       an index built at startup from the JSON files
//
// The file-backed store stays the default. It has no dependencies, it is what
// the site is live against today, and defaulting to the database would mean a
// deployment that had not yet been given a connection string would fail to
// start rather than carry on working.
//
// Anything other than these two values is refused rather than treated as the
// default. Silently falling back would let a typo in a deploy variable put
// production on the wrong store, serving stale content from a volume with
// nothing to say it had happened.
var contentStore = builder.Configuration["Content:Store"] ?? "file";

if (string.Equals(contentStore, "database", StringComparison.OrdinalIgnoreCase))
{
    // Owns the content connection: ConnectionStrings:Sw5e, a data source keyed
    // to this store, the content context and the database health check.
    // Identity is registered separately and resolves its own connection string,
    // so a deployment can give account data a least-privileged role without
    // touching any of this.
    builder.Services.AddSw5ePersistence(builder.Configuration);
    builder.Services.AddDatabaseContentStore();

    // Content authoring, registered here and only here.
    //
    // A write has to land somewhere it will be read from, and on the
    // file-backed store neither half of that holds: the content volume is
    // mounted read-only in every deployment, and the index is built once at
    // start-up and never reloaded, so even a write that somehow reached the
    // disk would stay invisible until the process restarted. Registering
    // authoring only alongside the database store means a file-backed
    // deployment cannot half-support it; the endpoints resolve the store
    // optionally and answer 503 saying exactly that.
    //
    // Content:SchemaPath points at the JSON Schemas the write path validates
    // against — the same documents the content repository's CI checks the whole
    // corpus with, evaluated by the same validator, which is why they are
    // consumed through a submodule rather than copied. Relative paths resolve
    // against the content root, and the image ships them beside the
    // application.
    var configuredSchemaPath = builder.Configuration["Content:SchemaPath"] ?? "schemas";

    builder.Services.AddContentAuthoring(
        Path.IsPathRooted(configuredSchemaPath)
            ? configuredSchemaPath
            : Path.Combine(builder.Environment.ContentRootPath, configuredSchemaPath));
}
else if (string.Equals(contentStore, "file", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IContentRepository>(services =>
    {
        // Relative paths resolve against the content root rather than the
        // current directory, which differs between `dotnet run`, a published
        // deployment and a test host.
        var configured = builder.Configuration["Content:RootPath"] ?? "content";
        var rootPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(builder.Environment.ContentRootPath, configured);

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Content);
        var result = FileContentRepository.Load(rootPath);

        // Warnings name files on disk, so they are logged and never returned. A
        // missing or half-populated content directory is a degraded catalogue,
        // not a failure to start: the content lives in a separate repository
        // that is still being filled in.
        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Content load: {Warning}", warning);
        }

        logger.LogInformation("Content index built with {ItemCount} items.", result.ItemCount);

        return result.Repository;
    });
}
else
{
    throw new InvalidOperationException(
        $"Content:Store is '{contentStore}'. It must be 'file' or 'database'.");
}

// Accounts: the identity store, the cookie policy, passkey configuration and
// the authorization policies. Everything security-relevant about them lives in
// AddSw5eIdentity rather than here, so that it can be reviewed in one piece.
builder.Services.AddSw5eIdentity(builder.Configuration);
builder.Services.AddScoped<AccountStateCookies>();
builder.Services.AddSw5eAuthRateLimiting(builder.Configuration);

// The proof-of-work challenge in front of registration and the emailed sign-in
// code. Self-hosted, with no third party involved and nothing loaded from
// anywhere else: the site's content security policy names no external host at
// all, so a hosted captcha would mean widening it, and widening it is the one
// change that turns "a script from this origin, and nothing else" into a
// standing exception somebody else controls.
//
// Off unless a deployment sets Auth:Challenge:Enabled and a secret. Enabled
// without a usable secret is a startup failure rather than a warning — see
// AddSw5eProofOfWork for why failing to start is the kinder outcome.
builder.Services.AddSw5eProofOfWork(builder.Configuration);

// Content flagging: the reports readers raise against the reference.
//
// Registered unconditionally, and separately from the content store, because
// the two answer different questions. Whether the catalogue is served from
// PostgreSQL or from JSON files is a deployment choice; where a user-submitted
// report is written is not, and a deployment serving content from files still
// has readers who can recognise an uncredited picture.
//
// The schema is its own, in its own PostgreSQL schema, with its own migration
// history — see Sw5eModerationDbContext for why it is neither in the content
// schema nor in the identity one. Which database it lands in is resolved by
// ModerationServiceCollectionExtensions, and the migrator resolves it through
// the same method so the two can never disagree.
//
// No migration runs here. As with content, schema is the migrator's job.
builder.Services.AddSw5eModeration(builder.Configuration);
builder.Services.AddSw5eFlagRateLimiting(builder.Configuration);

// Bridges the identity system's IAccountEmailSender onto the email library
// registered above. Registered after AddSw5eIdentity so it replaces the
// fail-closed stub that only exists to stop a deployment quietly pretending it
// sent anything.
builder.Services.AddSingleton<AccountEmailDeliveryMonitor>();
builder.Services.AddScoped<IAccountEmailSender, ProviderAccountEmailSender>();

// Where an undelivered account message ends up. The endpoints that send one
// cannot report the failure — their answer must not depend on whether mail got
// out, or it becomes a way to test whether an address has an account here — so
// the report comes out on this surface instead, alongside the error the sender
// logs.
//
// Degraded, never unhealthy: see AccountEmailHealthCheck for why a mail outage
// must not drain the instances that are still serving the rest of the site
// perfectly well.
builder.Services.AddHealthChecks()
       .AddCheck<AccountEmailHealthCheck>(
           AccountEmailHealthCheck.Name,
           failureStatus: HealthStatus.Degraded,
           tags: [AccountEmailHealthCheck.ReadyTag]);

var app = builder.Build();

// Resolve the store now rather than on the first request. For the file-backed
// store that forces the scan, so a content problem shows up in the startup log
// and the first visitor does not pay for it. For the database-backed store it
// only builds the object graph — no query is issued and no connection is
// opened, because an API that refused to start while its database was briefly
// unavailable would turn a short outage into a manual recovery.
app.Services.GetRequiredService<IContentRepository>();

// Must run before anything that reads the scheme or the client address,
// including the HSTS and HTTPS-redirection middleware below.
app.UseForwardedHeaders();

app.UseSw5eSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Rate limiting sits ahead of authentication so that a flood of sign-in
// attempts is refused before any of them costs a database read or a signature
// verification, and after UseForwardedHeaders so the partition key is the
// client's own address rather than the proxy's.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();

// One anonymous route carrying the handful of facts the browser application
// cannot work out for itself. It is prerendered HTML served by a static nginx
// image that is promoted from QA to production unchanged, so nothing in it
// varies by environment and nothing in it can be told; this service can be
// told, and already is. It answers which deployment this is — see
// SiteEnvironmentEndpoint for why that defaults to production when nobody has
// said otherwise — and whether account mail is currently getting out, which is
// how the site stops telling people to watch an inbox for a message the relay
// has just refused. The mail flag is global and carries no address and no
// provider reply, so it cannot be turned into a question about an account.
app.MapSiteEndpoints();
app.MapContentEndpoints();
app.MapAccountEndpoints();
app.MapFlagEndpoints();

// Authoring: drafting, publishing, history and revert.
//
// Mapped unconditionally even though the store behind it is registered only for
// the database content store. One route table in every deployment means a
// client gets the same answer shape everywhere, and an operator who has not
// enabled authoring gets a 503 that says so rather than a 404 that reads like a
// wrong URL.
app.MapAuthoringEndpoints();

if (app.Environment.IsDevelopment())
{
    // Anonymous explicitly. AddSw5eIdentity installs a fallback authorization
    // policy that denies any endpoint which has not said otherwise, and that
    // includes this one.
    app.MapOpenApi().AllowAnonymous();
}

app.Run();

// Exposed so that WebApplicationFactory<Program> can host the app in tests.
// This MUST stay in the global namespace: top-level statements emit their
// generated Program class there, and wrapping this declaration in a namespace
// would declare a different, unrelated type that never merges with it.
public partial class Program;
