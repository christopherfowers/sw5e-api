namespace Sw5e.Api.Security;

/// <summary>
/// Emits the platform's baseline security headers on every response. This runs
/// first in the pipeline so that error responses are covered too.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // The API serves JSON only. It never renders HTML, loads scripts, or frames
    // content, so the policy denies everything by default.
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'none'; " +
        "form-action 'none'";

    private const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), " +
        "magnetometer=(), microphone=(), payment=(), usb=()";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;

            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSw5eSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
