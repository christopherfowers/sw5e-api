using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Sw5e.Identity;

/// <summary>
/// The fallback authorization requirement: every mapped endpoint that has not
/// said otherwise needs a signed-in account, while a request that matches no
/// endpoint at all is left alone.
/// </summary>
/// <remarks>
/// <para>
/// The first half is the point. ASP.NET Core's default is that an endpoint with
/// no authorization metadata is public, so forgetting a
/// <c>RequireAuthorization</c> call silently ships an open endpoint. On a
/// platform where that mistake is a breach, the default should be the other way
/// round: closed until somebody writes down that it is open. The genuinely
/// public endpoints — the content catalogue, the health probes, the OpenAPI
/// document — all say <c>AllowAnonymous</c> explicitly, and now they have to.
/// </para>
/// <para>
/// The second half exists because the framework's own
/// <see cref="AuthorizationPolicy"/> fallback applies to unmatched requests
/// too. Left as-is, every typo in a URL answers 401 instead of 404: a public,
/// read-only content API starts telling anonymous visitors that a page they
/// mistyped requires them to sign in, and a client cannot distinguish a wrong
/// path from an expired session. The hiding it buys is worth very little
/// anyway, since the routes are published in the OpenAPI document.
/// </para>
/// </remarks>
internal sealed class MappedEndpointsRequireAuthorizationRequirement
    : IAuthorizationRequirement, IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        // The resource is the HttpContext for endpoint routing, which is how
        // this requirement can tell "no endpoint matched" from "an endpoint
        // matched and expressed no opinion". Anything else is treated as a
        // matched endpoint, which is the conservative reading.
        var matched = context.Resource is not HttpContext http || http.GetEndpoint() is not null;

        if (!matched || context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(this);
        }

        return Task.CompletedTask;
    }
}
