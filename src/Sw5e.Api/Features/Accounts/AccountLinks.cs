using Sw5e.Identity;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Builds the links that go into account email.
/// </summary>
/// <remarks>
/// <para>
/// The base URL comes from configuration and never from the request. That is
/// the whole reason this is a separate, single-purpose type: a handler with an
/// <see cref="HttpRequest"/> in scope will eventually be tempted to build the
/// link from <c>Request.Scheme</c> and <c>Request.Host</c>, and the day that
/// happens the platform will mail account-recovery links to whatever hostname
/// an attacker put in a <c>Host</c> header.
/// </para>
/// <para>
/// The links point at the browser application rather than at this API. The
/// front end reads the token out of the query string and posts it to
/// <c>/api/auth/email/verify</c>, which means the token is consumed by an
/// explicit action rather than by a preview fetch — mail clients and security
/// scanners follow links in messages, and a verification that completed on GET
/// would be spent before the recipient ever saw it.
/// </para>
/// <para>
/// A token in a query string is visible in browser history and, without care,
/// in referrer headers. Three things bound the exposure: the token is
/// single-use and dies on redemption, it expires within the hour, and the
/// platform sends <c>Referrer-Policy: no-referrer</c> on every response so it
/// is never forwarded to a third party.
/// </para>
/// </remarks>
internal static class AccountLinks
{
    /// <summary>The path on the browser application that handles a verification link.</summary>
    private const string VerifyPath = "account/verify";

    public static string VerifyEmail(Sw5eIdentityOptions options, string emailAddress, string token)
    {
        if (string.IsNullOrWhiteSpace(options.PublicSiteUrl))
        {
            // Refusing to send is the right failure. A link built against a
            // guessed base URL is a link that either goes nowhere or goes
            // somewhere else, and the second one is a credential leak.
            throw new InvalidOperationException(
                "'Identity:PublicSiteUrl' is not configured, so account email cannot be " +
                "addressed. Set it to the public base URL of the site.");
        }

        var baseUri = new Uri(options.PublicSiteUrl, UriKind.Absolute);

        return new UriBuilder(new Uri(baseUri, VerifyPath))
        {
            Query = $"email={Uri.EscapeDataString(emailAddress)}&token={Uri.EscapeDataString(token)}",
        }.Uri.ToString();
    }
}
