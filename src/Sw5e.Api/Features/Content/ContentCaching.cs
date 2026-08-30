using Microsoft.Net.Http.Headers;

namespace Sw5e.Api.Features.Content;

/// <summary>
/// Applies the validation and caching headers every content response carries.
/// </summary>
/// <remarks>
/// The catalogue is read-only and changes only when the content repository is
/// redeployed, so almost every request is for a body a caller already has. An
/// ETag turns the repeat into a 304 with no body, and a shared max-age lets a
/// CDN answer most of them without reaching the origin at all.
/// </remarks>
internal static class ContentCaching
{
    /// <summary>
    /// Stamps the response with its validator and cache policy, and reports
    /// whether the caller's copy is already current.
    /// </summary>
    /// <remarks>
    /// The headers are set before the answer is returned, so a 304 carries them
    /// too — a 304 that omits the ETag makes the next request unconditional
    /// again and undoes the saving.
    /// </remarks>
    /// <param name="context">The request being answered.</param>
    /// <param name="version">
    /// The store's version token for this exact response. Opaque here: the
    /// endpoint neither computes it nor interprets it.
    /// </param>
    /// <returns>True when the caller sent a matching <c>If-None-Match</c>.</returns>
    public static bool ApplyAndCheckFreshness(HttpContext context, string version)
    {
        var tag = new EntityTagHeaderValue($"\"{version}\"");
        var responseHeaders = context.Response.GetTypedHeaders();

        responseHeaders.ETag = tag;
        responseHeaders.CacheControl = new CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromSeconds(ContentRequestLimits.CacheSeconds),
        };

        foreach (var candidate in context.Request.GetTypedHeaders().IfNoneMatch)
        {
            // Weak comparison: a proxy is entitled to weaken a validator in
            // transit, and for a body that is byte-identical either way the
            // distinction buys nothing.
            if (candidate.Compare(tag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }
}
