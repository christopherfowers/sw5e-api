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
    /// How long an emailed sign-in code stays usable.
    /// </summary>
    /// <remarks>
    /// Long enough to find the message, read six digits and type them —
    /// including on a phone that fetches mail on a schedule — and short enough
    /// that a code left in an open inbox on a shared machine is worthless by
    /// the time anybody walks past. Ten minutes is the value nearly every
    /// service that sends these settles on, and readers have learned to expect
    /// it.
    /// </remarks>
    public TimeSpan EmailSignInCodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many codes one address may be sent inside
    /// <see cref="EmailSignInCodeBudgetWindow"/>.
    /// </summary>
    /// <remarks>
    /// This is the limit that stops the sign-in form being a mail cannon
    /// pointed at a stranger's inbox. It is counted against the address rather
    /// than the caller, because the caller is the attacker and the address is
    /// the victim: an attacker with a thousand IP addresses still gets three
    /// messages into any one mailbox per window.
    /// </remarks>
    public int EmailSignInCodesPerAddress { get; set; } = 3;

    /// <summary>The window <see cref="EmailSignInCodesPerAddress"/> is counted over.</summary>
    public TimeSpan EmailSignInCodeBudgetWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The shortest gap between two codes for the same address.
    /// </summary>
    /// <remarks>
    /// Separate from the budget because it answers a different abuse. The
    /// budget bounds the total; this bounds the rate, so a held-down "resend"
    /// button delivers one message rather than a burst, and it is what the
    /// front end counts down from before it re-enables the control.
    /// </remarks>
    public TimeSpan EmailSignInCodeResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many wrong codes may be tried against one issued code before it dies.
    /// </summary>
    /// <remarks>
    /// Five guesses against a million possibilities is a one-in-two-hundred-
    /// thousand chance per code. Combined with the per-address budget above,
    /// an attacker who controls the request side gets fifteen guesses per
    /// fifteen minutes against any one address, which is a chance of about one
    /// in seventy thousand per day of sustained effort. It also means a reader
    /// who fat-fingers the code twice is not sent back to the start.
    /// </remarks>
    public int EmailSignInCodeAttempts { get; set; } = 5;

    /// <summary>
    /// How many thirty-second steps either side of the current one an
    /// authenticator code is accepted from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single most common cause of "my authenticator app does not work" is
    /// a phone whose clock has drifted by a few seconds across a step boundary.
    /// A server that accepts only the current step rejects that person's
    /// perfectly correct code, and no message it could show them would explain
    /// why. One step either side — a ninety-second acceptance band — absorbs
    /// ordinary drift and the time it takes to read and type six digits.
    /// </para>
    /// <para>
    /// Widening it is not free: every extra step is another code that stays
    /// valid after it has left the screen, which lengthens the window in which
    /// a code read over somebody's shoulder or captured by a phishing page can
    /// still be replayed. One is the value the large providers use, and it is
    /// the value this platform should keep unless a deployment has a specific
    /// reason not to.
    /// </para>
    /// </remarks>
    public int AuthenticatorStepWindow { get; set; } = 1;

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

        if (EmailSignInCodeLifetime <= TimeSpan.Zero ||
            EmailSignInCodeBudgetWindow <= TimeSpan.Zero ||
            EmailSignInCodeResendCooldown < TimeSpan.Zero)
        {
            throw new Sw5eIdentityConfigurationException(
                "'Identity:EmailSignInCodeLifetime' and 'Identity:EmailSignInCodeBudgetWindow' " +
                "must be positive, and 'Identity:EmailSignInCodeResendCooldown' cannot be negative.");
        }

        if (EmailSignInCodesPerAddress < 1 || EmailSignInCodeAttempts < 1)
        {
            throw new Sw5eIdentityConfigurationException(
                "'Identity:EmailSignInCodesPerAddress' and 'Identity:EmailSignInCodeAttempts' " +
                "must be at least one. Setting either to zero would not harden the flow, it " +
                "would switch it off while leaving the endpoint answering as though it worked.");
        }

        // Zero is a legal value and a bad one, so it is allowed and the cost of
        // choosing it is stated in the option's own documentation rather than
        // being refused here. What is refused is a window so wide that a code
        // outlives the screen it was read from by minutes.
        if (AuthenticatorStepWindow is < 0 or > 4)
        {
            throw new Sw5eIdentityConfigurationException(
                "'Identity:AuthenticatorStepWindow' must be between 0 and 4 thirty-second steps. " +
                $"It is currently {AuthenticatorStepWindow}.");
        }
    }
}

/// <summary>Thrown during startup for identity configuration that cannot work.</summary>
public sealed class Sw5eIdentityConfigurationException(string message) : ValidationException(message);
