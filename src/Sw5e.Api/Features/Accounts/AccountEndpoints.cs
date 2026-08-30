using Microsoft.AspNetCore.RateLimiting;
using Sw5e.Api.Security;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// The account API: registration, email verification, passkey enrolment,
/// passkey sign-in, two-factor enrolment and verification, sign-out, and the
/// administrative role grant.
/// </summary>
/// <remarks>
/// <para>
/// Read the route table below as a security document rather than a list of
/// URLs. Every route states three things explicitly and never by inheritance:
/// who may call it, which rate-limit budget it draws on, and — for the
/// anonymous ones — that being anonymous is a decision somebody made rather
/// than an oversight.
/// </para>
/// <para>
/// The single most important property of this API is that <em>there is exactly
/// one way to obtain a session</em>: complete a passkey assertion, and then
/// satisfy any second factor the account has. Nothing else issues the session
/// cookie. Registering a passkey does not sign you in, verifying an email
/// address does not sign you in, and there is no password to check. That leaves
/// one code path to audit rather than four, and it is why an account with TOTP
/// enabled cannot be entered by any route that skips it.
/// </para>
/// </remarks>
internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes
            .MapGroup("/api/auth")
            .WithTags("Accounts")
            // Cross-site forgery defence for every unsafe method in the group,
            // applied at the group so a route added later cannot forget it.
            .AddEndpointFilter<CrossSiteRequestFilter>();

        MapRegistration(group);
        MapPasskeys(group);
        MapTwoFactor(group);
        MapSession(group);
        MapAdministration(group);

        return routes;
    }

    private static void MapRegistration(RouteGroupBuilder group)
    {
        group.MapPost("/register", RegistrationHandlers.RegisterAsync)
             .WithName("register")
             .WithSummary("Open a registration, or ask for a recovery link.")
             .WithDescription(
                 "Accepts an address and a display name and answers identically whether or not " +
                 "the address already has an account. A free address is registered and sent a " +
                 "verification link; an address that already belongs to a verified account is " +
                 "sent a passkey recovery link instead, because refusing would confirm to a " +
                 "stranger that the account exists. Either way the response is the same 202.")
             .Produces<RegisterResponse>(StatusCodes.Status202Accepted)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.SensitivePolicy);

        group.MapPost("/email/verify", RegistrationHandlers.VerifyEmailAsync)
             .WithName("verifyEmail")
             .WithSummary("Complete email verification.")
             .WithDescription(
                 "Consumes the token from the emailed link. On success the account's address is " +
                 "confirmed and a short enrolment window opens, during which — and only during " +
                 "which — a first passkey may be registered. Verifying does not sign the caller " +
                 "in; only a passkey assertion does that.")
             .Produces<VerifyEmailResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.SensitivePolicy);
    }

    private static void MapPasskeys(RouteGroupBuilder group)
    {
        // Anonymous, because the caller enrolling a first passkey has an
        // enrolment ticket rather than a session. The handler resolves the
        // account from a session or a ticket and refuses when it has neither,
        // so this route is anonymous to the router and authorised in the
        // handler — the one place in this API where those differ, and the
        // reason it is called out here.
        group.MapPost("/passkey/register/begin", PasskeyHandlers.BeginRegistrationAsync)
             .WithName("beginPasskeyRegistration")
             .WithSummary("Start enrolling a passkey.")
             .WithDescription(
                 "Returns WebAuthn credential creation options for navigator.credentials.create(). " +
                 "Available to a signed-in account adding another passkey, and to a caller " +
                 "holding an unexpired enrolment ticket from email verification.")
             .Produces<object>(contentType: "application/json")
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapPost("/passkey/register/complete", PasskeyHandlers.CompleteRegistrationAsync)
             .WithName("completePasskeyRegistration")
             .WithSummary("Finish enrolling a passkey.")
             .WithDescription(
                 "Verifies the attestation produced by navigator.credentials.create() against the " +
                 "challenge issued by the matching begin call and stores the credential. Does not " +
                 "sign the caller in: the client follows this with an ordinary passkey sign-in, so " +
                 "that a second factor cannot be stepped around by enrolling a new credential.")
             .Produces<PasskeyRegisteredResponse>(StatusCodes.Status201Created)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.SensitivePolicy);

        group.MapPost("/passkey/login/begin", PasskeyHandlers.BeginLoginAsync)
             .WithName("beginPasskeyLogin")
             .WithSummary("Start signing in with a passkey.")
             .WithDescription(
                 "Returns WebAuthn request options for navigator.credentials.get(). Takes no " +
                 "identifier and names no credentials: the browser chooses among the discoverable " +
                 "passkeys it holds for this site. The response is therefore identical for every " +
                 "caller and reveals nothing about which accounts exist.")
             .Produces<object>(contentType: "application/json")
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapPost("/passkey/login/complete", PasskeyHandlers.CompleteLoginAsync)
             .WithName("completePasskeyLogin")
             .WithSummary("Finish signing in with a passkey.")
             .WithDescription(
                 "Verifies the assertion and issues a session cookie, unless the account has a " +
                 "second factor, in which case it answers mfaRequired and the client posts a code " +
                 "to /api/auth/mfa/totp/verify. Every failure — unknown credential, bad signature, " +
                 "unverified address, locked-out account — is the same 401.")
             .Produces<SignInResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.SensitivePolicy);
    }

    private static void MapTwoFactor(RouteGroupBuilder group)
    {
        group.MapPost("/mfa/totp/enroll", TwoFactorHandlers.EnrollAsync)
             .WithName("enrollTotp")
             .WithSummary("Begin authenticator app enrolment.")
             .WithDescription(
                 "Issues a fresh TOTP secret and the otpauth:// URI to render as a QR code. " +
                 "Two-factor authentication is not switched on until a code from that secret is " +
                 "verified, so an interrupted enrolment cannot lock anybody out.")
             .Produces<TotpEnrollmentResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        // Anonymous at the router because this route serves two callers: a
        // signed-in account finishing enrolment, and a caller who has passed a
        // passkey assertion and holds nothing but the pending two-factor
        // cookie. The second is by definition not yet authenticated. Which of
        // the two is in play is decided by cookie state, never by anything in
        // the request body, and a caller with neither gets a 401.
        group.MapPost("/mfa/totp/verify", TwoFactorHandlers.VerifyAsync)
             .WithName("verifyTotp")
             .WithSummary("Verify a code from the authenticator app.")
             .WithDescription(
                 "Completes enrolment for a signed-in account, returning its recovery codes; or " +
                 "completes a sign-in for a caller waiting on a second factor, returning a session. " +
                 "Repeated wrong codes count against the account lockout.")
             .Produces<TotpEnabledResponse>()
             .Produces<SignInResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.SensitivePolicy);
    }

    private static void MapSession(RouteGroupBuilder group)
    {
        group.MapPost("/logout", SessionHandlers.LogoutAsync)
             .WithName("logout")
             .WithSummary("End the session.")
             .WithDescription(
                 "Clears the session cookie along with any half-finished sign-in or enrolment " +
                 "state. Anonymous and idempotent on purpose: a caller whose session has already " +
                 "expired still needs the cookies cleared, and answering 401 would leave them there.")
             .Produces(StatusCodes.Status204NoContent)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapGet("/me", SessionHandlers.GetCurrentUserAsync)
             .WithName("currentUser")
             .WithSummary("The signed-in account.")
             .WithDescription(
                 "The caller's own account, with the roles that decide what it may do. " +
                 "Answers 401 when there is no session.")
             .Produces<CurrentUserResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);
    }

    private static void MapAdministration(RouteGroupBuilder group)
    {
        // Not part of the original endpoint list, and added because without it
        // the Contributor role can only ever be granted by someone with a
        // database client — which means the privilege that gates content upload
        // has no reviewable, audited path to being handed out.
        group.MapPut("/admin/users/{userId:guid}/roles", AdministrationHandlers.AssignRolesAsync)
             .WithName("assignRoles")
             .WithSummary("Set the roles held by an account.")
             .WithDescription(
                 "Administrators only. Declares the roles the account should hold afterwards; any " +
                 "assignable role not listed is revoked. The account is emailed about the change, " +
                 "and its live sessions are re-evaluated within minutes rather than at expiry.")
             .Produces<AccountRolesResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);
    }
}
