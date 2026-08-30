using System.ComponentModel.DataAnnotations;

namespace Sw5e.Identity;

/// <summary>
/// Everything about the identity stack that changes between deployments.
/// </summary>
/// <remarks>
/// Bound from the <c>Identity</c> configuration section. Nothing here is a
/// secret — the connection string is the one exception and it is expected to
/// arrive from the environment, never from a committed file.
/// </remarks>
public sealed class Sw5eIdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>
    /// The PostgreSQL connection string for the identity database.
    /// </summary>
    /// <remarks>
    /// Read from <c>Identity:ConnectionString</c> if set, otherwise from the
    /// standard <c>ConnectionStrings:Sw5eIdentity</c> and
    /// <c>ConnectionStrings:Sw5e</c> keys in that order. Identity is allowed
    /// its own credential — and should have one, with rights over nothing but
    /// the <c>identity</c> schema — but sharing the platform connection string
    /// is supported so a small deployment is not forced to run two roles.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The WebAuthn Relying Party ID: the registrable domain passkeys are bound
    /// to, such as <c>sw5e.example</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single most security-critical value in this file. A passkey
    /// is cryptographically bound to the RP ID at creation time, and the
    /// authenticator refuses to sign for any other one. Getting it wrong does
    /// not weaken authentication, it breaks it: every existing passkey stops
    /// working, and every new one is bound to the wrong name.
    /// </para>
    /// <para>
    /// It must be the site's registrable domain with no scheme, no port and no
    /// path — <c>sw5e.example</c>, not <c>https://sw5e.example/</c>. It may be
    /// a parent of the origin's host (an origin of <c>app.sw5e.example</c> may
    /// use an RP ID of <c>sw5e.example</c>) but never a child and never an
    /// unrelated domain.
    /// </para>
    /// <para>
    /// Left unset, the framework falls back to the origin's own host, which is
    /// correct for local development on <c>localhost</c> and wrong for anything
    /// served under more than one hostname. Set it in every deployed
    /// environment.
    /// </para>
    /// </remarks>
    public string? RelyingPartyId { get; set; }

    /// <summary>
    /// The exact origins the browser application is served from, such as
    /// <c>https://sw5e.example</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two separate defences read this list: WebAuthn origin validation, which
    /// decides whether a credential produced for some other site may be
    /// presented here, and the cross-site request check that stands in for
    /// anti-forgery tokens on this cookie-authenticated API.
    /// </para>
    /// <para>
    /// Empty means "same origin only", which is the correct and safest value
    /// whenever the site and the API are served from one hostname through the
    /// reverse proxy. Add entries only for a front end genuinely hosted
    /// somewhere else, and add exact origins — scheme, host and port — because
    /// they are compared exactly. There is no wildcard and there will not be
    /// one.
    /// </para>
    /// </remarks>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// The public base URL of the browser application, used to build the links
    /// sent by email, such as <c>https://sw5e.example</c>.
    /// </summary>
    /// <remarks>
    /// Configured rather than derived from the incoming request. Deriving it
    /// would mean an attacker who can set the <c>Host</c> header decides where
    /// a verification link points, which turns the account recovery email into
    /// a token-harvesting service. Required before any account email can be
    /// sent.
    /// </remarks>
    public string? PublicSiteUrl { get; set; }

    /// <summary>
    /// How long a session cookie stays valid without activity.
    /// </summary>
    /// <remarks>
    /// Sliding, so an active session is not interrupted, but an abandoned one
    /// on a shared machine expires the same day. Eight hours is one working
    /// session.
    /// </remarks>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// How long a link emailed to a user stays usable.
    /// </summary>
    /// <remarks>
    /// Applies to email verification and passkey recovery alike. The framework
    /// default is a full day; an hour is long enough for somebody to find the
    /// message and short enough that a link sitting in a mailbox backup is not
    /// a standing key to the account. The token is additionally bound to the
    /// account's security stamp, so using one burns the rest.
    /// </remarks>
    public TimeSpan EmailTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Applies pending migrations and seeds the role table during startup.
    /// </summary>
    /// <remarks>
    /// Off by default. A web process that migrates its own database is a web
    /// process holding schema-modification rights at runtime, and several
    /// replicas starting at once will race each other for the migration lock.
    /// Production runs migrations as a deliberate, separate step; this switch
    /// exists for local development and for the integration tests, which need
    /// a schema to exist before the first request.
    /// </remarks>
    public bool InitializeDatabaseAtStartup { get; set; }

    /// <summary>
    /// An email address that is granted the administrator role during
    /// initialisation, if an account with that address already exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This solves the bootstrap problem — only an administrator can grant the
    /// administrator role, so the first one has to come from somewhere — while
    /// creating nothing. No account is created, no credential is set, no
    /// password is invented. The named person registers through the ordinary
    /// public flow, proves control of the address by email, and enrols a
    /// passkey exactly like anybody else; this setting only decides which of
    /// the resulting accounts is promoted.
    /// </para>
    /// <para>
    /// It is therefore safe if it leaks: knowing which address will be promoted
    /// buys an attacker nothing they could not learn from the site's own credits
    /// page, and it never confers access without that mailbox.
    /// </para>
    /// </remarks>
    public string? BootstrapAdministratorEmail { get; set; }

    /// <summary>
    /// Fails fast on values that would otherwise produce a subtly broken
    /// deployment rather than an obviously broken one.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new Sw5eIdentityConfigurationException(
                "No identity database connection string is configured. Set " +
                "'Identity:ConnectionString', 'ConnectionStrings:Sw5eIdentity' or " +
                "'ConnectionStrings:Sw5e'.");
        }

        // A scheme, port or slash here means somebody pasted an origin where a
        // domain belongs. Left alone it produces passkeys nobody can use, and
        // the symptom shows up at the authenticator rather than at startup.
        if (RelyingPartyId is not null &&
            RelyingPartyId.AsSpan().IndexOfAny(":/") >= 0)
        {
            throw new Sw5eIdentityConfigurationException(
                "'Identity:RelyingPartyId' must be a bare domain such as 'sw5e.example', " +
                $"with no scheme, port or path. It is currently '{RelyingPartyId}'.");
        }

        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new Sw5eIdentityConfigurationException(
                    "'Identity:AllowedOrigins' entries must be absolute origins such as " +
                    $"'https://sw5e.example', with no path or query. '{origin}' is not.");
            }
        }

        if (PublicSiteUrl is not null && !Uri.TryCreate(PublicSiteUrl, UriKind.Absolute, out _))
        {
            throw new Sw5eIdentityConfigurationException(
                $"'Identity:PublicSiteUrl' must be an absolute URL. It is currently '{PublicSiteUrl}'.");
        }

        if (SessionLifetime <= TimeSpan.Zero || EmailTokenLifetime <= TimeSpan.Zero)
        {
            throw new Sw5eIdentityConfigurationException(
                "'Identity:SessionLifetime' and 'Identity:EmailTokenLifetime' must be positive.");
        }
    }
}

/// <summary>Thrown during startup for identity configuration that cannot work.</summary>
public sealed class Sw5eIdentityConfigurationException(string message) : ValidationException(message);
