using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Sw5e.Api.Features.Accounts;
using Sw5e.Api.Features.Content;
using Sw5e.Api.Features.Health;
using Sw5e.Api.Security;
using Sw5e.Domain.Content;
using Sw5e.Email.Configuration;
using Sw5e.Identity;
using Sw5e.Identity.Email;
using Sw5e.Infrastructure.Content;

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

// The content store is registered behind IContentRepository so the endpoints
// never learn where the catalogue actually lives. The intended home is
// PostgreSQL; until that exists, the same contract is satisfied by an index
// built from the JSON content files. Swapping the two is this one registration.
builder.Services.AddSingleton<IContentRepository>(services =>
{
    // Relative paths resolve against the content root rather than the current
    // directory, which differs between `dotnet run`, a published deployment and
    // a test host.
    var configured = builder.Configuration["Content:RootPath"] ?? "content";
    var rootPath = Path.IsPathRooted(configured)
        ? configured
        : Path.Combine(builder.Environment.ContentRootPath, configured);

    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Sw5e.Api.Content");
    var result = FileContentRepository.Load(rootPath);

    // Warnings name files on disk, so they are logged and never returned. A
    // missing or half-populated content directory is a degraded catalogue, not
    // a failure to start: the content lives in a separate repository that is
    // still being filled in.
    foreach (var warning in result.Warnings)
    {
        logger.LogWarning("Content load: {Warning}", warning);
    }

    logger.LogInformation("Content index built with {ItemCount} items.", result.ItemCount);

    return result.Repository;
});

// Accounts: the identity store, the cookie policy, passkey configuration and
// the authorization policies. Everything security-relevant about them lives in
// AddSw5eIdentity rather than here, so that it can be reviewed in one piece.
builder.Services.AddSw5eIdentity(builder.Configuration);
builder.Services.AddScoped<AccountStateCookies>();
builder.Services.AddSw5eAuthRateLimiting(builder.Configuration);

// Bridges the identity system's IAccountEmailSender onto the email library
// registered above. Registered after AddSw5eIdentity so it replaces the
// fail-closed stub that only exists to stop a deployment quietly pretending it
// sent anything.
builder.Services.AddScoped<IAccountEmailSender, ProviderAccountEmailSender>();

var app = builder.Build();

// Force the index to be built now rather than on the first request, so a
// content problem shows up in the startup log and the first visitor does not
// pay for the scan.
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
app.MapContentEndpoints();
app.MapAccountEndpoints();

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
