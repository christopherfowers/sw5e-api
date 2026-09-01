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
    /// <remarks>
    /// <para>
    /// This must match a route the site actually serves, and it is not a free
    /// choice. It used to read <c>account/verify</c>, which the site does not
    /// serve at all: the browser application prerenders a fixed list of paths
    /// and answers anything else with its not-found page, so every verification
    /// and recovery link this service ever sent led nowhere. Nothing caught it,
    /// because the two repositories were tested separately and neither test
    /// suite knew what the other side's route table said.
    /// </para>
    /// <para>
    /// It also could not have lived under <c>/account</c> even if that route had
    /// existed. Everything below that path is behind the site's session guard,
    /// and the entire point of this link is that it is opened by somebody who
    /// has no session and no credential yet — a brand-new account whose only
    /// proof of anything is the message in their inbox.
    /// </para>
    /// <para>
    /// The route is listed in the site's prerender configuration and in
    /// <c>docs/account-api-contract.md</c>. Changing it here means changing it
    /// there.
    /// </para>
    /// </remarks>
    private const string VerifyPath = "verify-email";

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
