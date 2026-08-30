using Microsoft.Extensions.Options;
using Sw5e.Identity;

namespace Sw5e.Api.Security;

/// <summary>
/// Refuses state-changing requests that did not come from this site.
/// </summary>
/// <remarks>
/// <para>
/// This is the anti-forgery defence for a cookie-authenticated API, and it is
/// deliberately not a synchroniser token. Tokens exist because a server-rendered
/// form has somewhere to put one; an API whose only client is a script running
/// on the same origin has no such place, and bolting one on means shipping a
/// token to JavaScript, which reintroduces the exact readable-credential
/// problem that choosing cookies over bearer tokens was meant to avoid.
/// </para>
/// <para>
/// What actually stops cross-site forgery here is three independent layers, and
/// this filter is the second:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>SameSite=Strict</c> on the session cookie. The browser does not attach it
/// to any request initiated from another site, so a forged request arrives
/// unauthenticated and is rejected as such. This is the strongest layer and it
/// needs no code. It is also the one that silently stops working the day
/// somebody relaxes the cookie to <c>Lax</c> for a login-link convenience,
/// which is precisely why it is not the only layer.
/// </description>
/// </item>
/// <item>
/// <description>
/// This filter: every unsafe request must positively identify itself as coming
/// from an origin this deployment serves. A cross-site form post carries an
/// <c>Origin</c> the browser sets and the page cannot forge, so it is refused
/// here even if a cookie somehow accompanied it.
/// </description>
/// </item>
/// <item>
/// <description>
/// A JSON body. HTML forms — the only way to make a browser issue a
/// cross-origin POST without CORS approval — can send exactly three content
/// types, and <c>application/json</c> is not among them. Minimal APIs reject
/// anything else with a 415 before a handler runs, so the simple-request escape
/// hatch is closed by the framework rather than by anything written here.
/// </description>
/// </item>
/// </list>
/// <para>
/// The filter fails closed. A request with neither an <c>Origin</c> header nor
/// a <c>Sec-Fetch-Site</c> header is refused rather than waved through: every
/// browser has sent <c>Origin</c> on unsafe requests for years, so the absence
/// of one means the caller is not the browser application this API exists to
/// serve. Command-line clients are affected, and that is the intended trade —
/// they can set the header.
/// </para>
/// </remarks>
internal sealed class CrossSiteRequestFilter(IOptions<Sw5eIdentityOptions> options) : IEndpointFilter
{
    private readonly Sw5eIdentityOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;

        // Safe methods change nothing, so forging one achieves nothing. The
        // account API has no safe method that returns anything an attacker's
        // page could read anyway — the browser's same-origin policy sees to
        // that — but the exemption is stated rather than assumed.
        if (HttpMethods.IsGet(request.Method) ||
            HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method))
        {
            return await next(context);
        }

        if (!IsSameSite(request))
        {
            // No Problem Details body and no explanation. A response that
            // explains which header was missing is a response that helps
            // somebody tune an attack, and a legitimate client is never in this
            // branch.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }

    private bool IsSameSite(HttpRequest request)
    {
        // Sec-Fetch-Site is set by the browser, cannot be set by script, and
        // states the relationship directly. When it is present and says the
        // request came from this exact origin, that is the strongest signal
        // available and nothing further is needed.
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite) &&
            string.Equals(fetchSite, "same-origin", StringComparison.Ordinal))
        {
            return true;
        }

        // Otherwise the Origin header decides. It is also browser-set and also
        // unforgeable from script, but it has to be compared against the
        // origins this deployment actually serves, because "some origin sent
        // this" is not the same statement as "we serve that origin".
        if (!request.Headers.TryGetValue("Origin", out var originHeader) ||
            !Uri.TryCreate(originHeader.ToString(), UriKind.Absolute, out var origin))
        {
            return false;
        }

        foreach (var allowed in _options.AllowedOrigins)
        {
            if (Uri.TryCreate(allowed, UriKind.Absolute, out var candidate) &&
                Matches(origin, candidate))
            {
                return true;
            }
        }

        // With no allow-list configured the only acceptable origin is the one
        // the request was addressed to: the single-hostname deployment behind
        // the reverse proxy. Request.Scheme is trustworthy here because
        // forwarded headers have already been applied, and Request.Host is
        // constrained by the AllowedHosts setting.
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(Uri origin, Uri allowed) =>
        Uri.Compare(
            origin,
            allowed,
            UriComponents.SchemeAndServer,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
}
