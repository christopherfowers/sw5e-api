using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sw5e.Identity.Email;

namespace Sw5e.Identity;

/// <summary>
/// Registers the whole identity stack: the store, the managers, the cookie
/// policy, the passkey configuration and the authorization policies.
/// </summary>
/// <remarks>
/// One entry point on purpose. Authentication configuration that is spread
/// across a composition root is configuration nobody can audit, and the
/// questions a reviewer needs to answer — is the session cookie
/// <c>HttpOnly</c>, does an unverified account get in, how many failures
/// before a lockout — should all be answerable by reading one file.
/// </remarks>
public static class Sw5eIdentityServiceCollectionExtensions
{
    /// <summary>
    /// The session cookie's name.
    /// </summary>
    /// <remarks>
    /// The <c>__Host-</c> prefix is not decoration. A browser refuses to store
    /// a cookie with this prefix unless it is <c>Secure</c>, has
    /// <c>Path=/</c> and carries no <c>Domain</c> attribute — which means no
    /// sibling subdomain can set it, and nothing served over plain HTTP can
    /// either. That closes cookie fixation and subdomain-takeover cookie
    /// injection at the browser, where it holds even if a future change here
    /// gets the server-side flags wrong.
    /// </remarks>
    public const string SessionCookieName = "__Host-sw5e.session";

    /// <summary>The cookie carrying "this account has passed its first factor".</summary>
    public const string TwoFactorCookieName = "__Host-sw5e.mfa";

    public static IServiceCollection AddSw5eIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BindOptions(configuration);
        options.Validate();
        services.AddSingleton(Options.Create(options));

        AddStore(services, options);
        AddDataProtection(services);
        AddIdentityCore(services, options);
        AddCookiePolicy(services, options);
        AddPasskeyPolicy(services, options);
        AddAuthorizationPolicies(services);

        // Fails closed: if nothing else supplies a mail provider, the first
        // attempt to send throws rather than pretending it delivered. See
        // UnconfiguredAccountEmailSender for why a no-op would be a bug.
        services.TryAddScoped<IAccountEmailSender, UnconfiguredAccountEmailSender>();

        if (options.InitializeDatabaseAtStartup)
        {
            services.AddHostedService<Sw5eIdentityInitializer>();
        }

        return services;
    }

    private static Sw5eIdentityOptions BindOptions(IConfiguration configuration)
    {
        var options = new Sw5eIdentityOptions();
        configuration.GetSection(Sw5eIdentityOptions.SectionName).Bind(options);

        // The dedicated key wins, then the platform-wide one. Identity is happy
        // to share the platform connection string, but a deployment that wants
        // a least-privileged role for account data can hand it one without
        // touching anything else.
        options.ConnectionString ??=
            configuration.GetConnectionString("Sw5eIdentity") ??
            configuration.GetConnectionString("Sw5e");

        return options;
    }

    private static void AddStore(IServiceCollection services, Sw5eIdentityOptions options)
    {
        services.AddDbContext<Sw5eIdentityDbContext>(builder => builder
            .UseNpgsql(options.ConnectionString, npgsql => npgsql
                // The migration history table lives in the identity schema
                // alongside the tables it describes. Left in the default schema
                // it would sit in whatever the content store also calls home,
                // and two independent migration streams sharing one history
                // table is a corruption waiting for a deploy to trigger it.
                .MigrationsHistoryTable("__EFMigrationsHistory", Sw5eIdentityDbContext.Schema)));
    }

    private static void AddDataProtection(IServiceCollection services)
    {
        // Data protection keys sign and encrypt the session cookie, the
        // two-factor cookie, the passkey challenge cookies and every token
        // emailed to a user. Left on the default file-system key ring inside a
        // container they are lost on every restart — silently logging every
        // user out and invalidating every outstanding verification link — and
        // are not shared between replicas at all, so a two-replica deployment
        // rejects half its own cookies.
        //
        // Persisting them into the identity schema fixes both: the ring is
        // durable, it is shared, and it is backed up by whatever already backs
        // up the account data it protects. It also keeps the deployment free of
        // a writable volume that would otherwise have to exist purely for keys.
        services.AddDataProtection()
                .PersistKeysToDbContext<Sw5eIdentityDbContext>()
                .SetApplicationName("Sw5e");
    }

    private static void AddIdentityCore(IServiceCollection services, Sw5eIdentityOptions options)
    {
        services
            .AddIdentityCore<Sw5eUser>(identity =>
            {
                // Passkey storage exists only from schema version 3, and the
                // framework defaults to version 1. See Sw5eIdentitySchema for
                // what goes wrong when the runtime and the migration disagree.
                identity.Stores.SchemaVersion = Sw5eIdentitySchema.Version;

                // An account that has not proved control of its address cannot
                // sign in. Without this an attacker registers somebody else's
                // address, enrols their own passkey, and owns an account
                // wearing a stranger's identity; the real owner then cannot
                // register because the address is taken.
                identity.SignIn.RequireConfirmedEmail = true;
                identity.SignIn.RequireConfirmedAccount = true;

                // Uniqueness is enforced in the database by a unique index as
                // well. This setting is what makes the framework check first
                // and return a clean result instead of a constraint violation.
                identity.User.RequireUniqueEmail = true;

                // Lockout applies to every account from the moment it is
                // created, new ones included. The framework's default excludes
                // new users, which would leave the freshest accounts — the ones
                // an attacker just created a target list from — as the only
                // ones with unlimited attempts.
                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                // Six digits over a thirty-second window is 5 000 guesses per
                // hour at one attempt per try. Five attempts and a fifteen
                // minute pause reduces that to twenty per hour, which is a
                // rounding error against a million-code space.

                // No flow here sets a password and none reads one. These bounds
                // exist so that if some future code path ever does, it cannot
                // do it weakly by accident.
                identity.Password.RequiredLength = 16;
                identity.Password.RequireDigit = true;
                identity.Password.RequireLowercase = true;
                identity.Password.RequireUppercase = true;
                identity.Password.RequireNonAlphanumeric = true;

                // Shown in the authenticator app beside the account's code.
                identity.Tokens.AuthenticatorIssuer = "SW5e";
            })
            .AddRoles<Sw5eRole>()
            .AddEntityFrameworkStores<Sw5eIdentityDbContext>()
            .AddSignInManager()
            // Supplies the authenticator (TOTP) provider used for two-factor
            // enrolment and verification, and the data-protection token
            // provider behind email verification and recovery.
            .AddDefaultTokenProviders();

        // Applies to the email verification and recovery tokens.
        services.Configure<DataProtectionTokenProviderOptions>(
            tokens => tokens.TokenLifespan = options.EmailTokenLifetime);

        // How often a live session is re-checked against the account's security
        // stamp. The framework default is thirty minutes, which is how long a
        // stolen session survives after the account is locked, its roles are
        // revoked or its passkeys are removed. Five minutes is a far more
        // defensible ceiling on "revoked but still working", and the check is a
        // single indexed read.
        services.Configure<SecurityStampValidatorOptions>(
            validator => validator.ValidationInterval = TimeSpan.FromMinutes(5));
    }

    private static void AddCookiePolicy(IServiceCollection services, Sw5eIdentityOptions options)
    {
        services
            .AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies();

        // The session cookie. This is the credential for every authenticated
        // request, so every attribute below is load-bearing.
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = SessionCookieName;

            // Unreachable from JavaScript. The whole reason this API uses a
            // cookie rather than a bearer token is that a token has to live
            // somewhere script can reach, which makes any cross-site scripting
            // bug anywhere on the origin an immediate credential theft.
            cookie.Cookie.HttpOnly = true;

            // Never sent over plain HTTP, in any environment. Not
            // SameAsRequest: that would emit a non-Secure cookie the one time
            // it mattered, on a request that reached the app over HTTP because
            // a proxy header was missing.
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;

            // Strict, not Lax. Lax still attaches the cookie to top-level
            // cross-site GET navigations, which is enough for an attacker's
            // page to navigate a victim into an authenticated state and read
            // what comes back through a side channel. The cost is that
            // following a link from an email lands logged out until the
            // application makes its first same-site request, which for a
            // single-page front end is invisible.
            cookie.Cookie.SameSite = SameSiteMode.Strict;

            // Required by the __Host- prefix, and correct anyway.
            cookie.Cookie.Path = "/";
            cookie.Cookie.IsEssential = true;

            cookie.ExpireTimeSpan = options.SessionLifetime;
            cookie.SlidingExpiration = true;

            // This is an API. The framework's default is to answer an
            // unauthenticated request with a 302 to a login page that does not
            // exist here, which a fetch() client sees as a successful
            // navigation to nowhere. Answer with the status codes the contract
            // promises instead.
            //
            // The body matters as much as the status. Setting the status alone
            // produces a 401 with no content type and no payload, which is
            // indistinguishable — to a client that decides what happened by
            // looking at the body — from a reverse proxy answering while the
            // API is not mounted. A browser client that made that mistake would
            // tell every signed-out reader the service was unreachable instead
            // of offering them a way to sign in. Every other refusal in this
            // API is a problem document, so these two are as well.
            cookie.Events.OnRedirectToLogin = context => WriteProblemAsync(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "This request requires a signed-in account.");

            cookie.Events.OnRedirectToAccessDenied = context => WriteProblemAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "This account may not perform that action.");

            cookie.Events.OnRedirectToLogout = context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };
        });

        // The cookie that says "this account passed its first factor and is
        // waiting on its second". It is a partial credential and is treated
        // like one: the same flags as the session cookie, and a lifetime
        // measured in the time it takes to read six digits off a phone.
        services.Configure<CookieAuthenticationOptions>(
            IdentityConstants.TwoFactorUserIdScheme,
            cookie =>
            {
                cookie.Cookie.Name = TwoFactorCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.Path = "/";
                cookie.Cookie.IsEssential = true;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                cookie.SlidingExpiration = false;
            });

        // Neither of the remaining identity cookies is used by any flow here —
        // there are no external login providers, and no flow asks to be
        // remembered past its second factor — but the schemes are registered by
        // AddIdentityCookies, so they are locked down rather than left on
        // framework defaults in case something later reaches for one.
        foreach (var scheme in new[]
                 {
                     IdentityConstants.ExternalScheme,
                     IdentityConstants.TwoFactorRememberMeScheme,
                 })
        {
            services.Configure<CookieAuthenticationOptions>(scheme, cookie =>
            {
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.Path = "/";
            });
        }
    }

    private static void AddPasskeyPolicy(IServiceCollection services, Sw5eIdentityOptions options)
    {
        services.Configure<IdentityPasskeyOptions>(passkey =>
        {
            // Null is a supported value and means "use the origin's host",
            // which is what makes local development on localhost work without
            // configuration. Every deployed environment sets it.
            passkey.ServerDomain = options.RelyingPartyId;

            // Require the authenticator to verify the human — biometric, PIN or
            // equivalent — before it will sign. This is what makes a single
            // passkey two factors rather than one: possession of the
            // authenticator, plus something only its owner can supply. It is
            // also the framework default, restated because the whole
            // authentication design rests on it.
            passkey.UserVerificationRequirement = "required";

            // Discoverable (resident) credentials, and the reason is account
            // enumeration. A non-discoverable credential has to be named in the
            // request's allowCredentials list, which means the server must be
            // told which account is signing in before it can build the
            // challenge, which means the sign-in endpoint takes an email
            // address and answers differently depending on whether it exists.
            // Requiring discoverable credentials lets the browser pick the
            // account, so the sign-in challenge is identical for everybody and
            // reveals nothing at all.
            passkey.ResidentKeyRequirement = "required";

            // Long enough to find a phone, short enough that an abandoned
            // challenge is not left standing.
            passkey.AuthenticatorTimeout = TimeSpan.FromMinutes(2);

            // 32 bytes of challenge. Restated rather than inherited because a
            // shorter challenge is the difference between a replay being
            // impossible and being merely unlikely.
            passkey.ChallengeSize = 32;

            // The framework's default accepts any origin that matches the
            // credential's own and refuses cross-origin outright, which is
            // already sound. This narrows it further to the origins this
            // deployment actually serves, so a credential produced against some
            // other host that happens to share the relying party domain is
            // still refused.
            passkey.ValidateOrigin = context =>
                ValueTask.FromResult(IsAllowedPasskeyOrigin(context, options));
        });
    }

    private static bool IsAllowedPasskeyOrigin(
        PasskeyOriginValidationContext context,
        Sw5eIdentityOptions options)
    {
        // An iframe on somebody else's page is never a legitimate place to
        // present a credential for this site.
        if (context.CrossOrigin)
        {
            return false;
        }

        if (!Uri.TryCreate(context.Origin, UriKind.Absolute, out var origin))
        {
            return false;
        }

        // Configured allow-list first, compared as origins rather than as
        // strings so a trailing slash is not the difference between working and
        // not.
        foreach (var allowed in options.AllowedOrigins)
        {
            if (Uri.TryCreate(allowed, UriKind.Absolute, out var candidate) &&
                Uri.Compare(origin, candidate, UriComponents.SchemeAndServer,
                    UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }
        }

        // With no allow-list configured, the request's own origin is the only
        // acceptable one — the same-origin deployment behind the reverse proxy.
        var request = context.HttpContext.Request;
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes an RFC 9457 problem document for a refusal raised by the
    /// authentication handler rather than by an endpoint.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="IProblemDetailsService"/> so these two answers
    /// are shaped by the same configuration, and carry the same trace
    /// identifier, as every refusal an endpoint produces. Falling back to a
    /// bare status code if the service is not registered keeps this from being
    /// the reason a request fails.
    /// </remarks>
    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.StatusCode = statusCode;

        if (context.RequestServices.GetService<IProblemDetailsService>() is not { } problems)
        {
            return;
        }

        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
            },
        });
    }

    private static void AddAuthorizationPolicies(IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            // Deny by default, for mapped endpoints only. See
            // MappedEndpointsRequireAuthorizationRequirement for both halves of
            // that sentence.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .AddRequirements(new MappedEndpointsRequireAuthorizationRequirement())
                .Build())
            // Authorization is deny-by-default at the policy level too: a
            // policy always demands an authenticated principal before it looks
            // at a role, so a misconfigured role name can never silently admit
            // anonymous callers.
            .AddPolicy(Sw5ePolicies.SignedIn, policy => policy
                .RequireAuthenticatedUser())
            .AddPolicy(Sw5ePolicies.Contribute, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Sw5eRoles.Contributor, Sw5eRoles.Administrator))
            .AddPolicy(Sw5ePolicies.Administer, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(Sw5eRoles.Administrator));
    }
}
