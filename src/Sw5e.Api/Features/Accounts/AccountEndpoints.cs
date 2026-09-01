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
        MapEmailCodes(group);
        MapPasskeys(group);
        MapTwoFactor(group);
        MapReauthentication(group);
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

    private static void MapEmailCodes(RouteGroupBuilder group)
    {
        // Anonymous, because the entire point is to admit somebody who has no
        // credential on this device. It is also the only anonymous endpoint on
        // the platform that causes a message to be delivered to an address the
        // caller names, which is why it draws on its own, much smaller budget.
        group.MapPost("/email/code", EmailCodeHandlers.RequestAsync)
             .WithName("requestSignInCode")
             .WithSummary("Ask for a one-time sign-in code by email.")
             .WithDescription(
                 "Sends a short numeric code to the address, and answers identically whether or " +
                 "not that address has an account: same status, same body, same amount of work, " +
                 "and one message either way. An address with an account is sent the code; an " +
                 "address without one is sent a note saying somebody tried to sign in and that " +
                 "there is nothing here. Refusing to send to the second, or answering it " +
                 "differently, would turn this endpoint into a way to test whether a given " +
                 "person has an account. Rate limited per caller here and per address in the " +
                 "handler; the throttled case answers 202 as well.")
             .Produces<SignInCodeRequestedResponse>(StatusCodes.Status202Accepted)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .AllowAnonymous()
             .RequireRateLimiting(AuthRateLimiting.EmailCodePolicy);

        group.MapPost("/email/code/verify", EmailCodeHandlers.VerifyAsync)
             .WithName("verifySignInCode")
             .WithSummary("Sign in with an emailed code.")
             .WithDescription(
                 "Redeems a code and issues a session, unless the account has an authenticator " +
                 "app, in which case it answers mfaRequired and the client posts a code to " +
                 "/api/auth/mfa/totp/verify exactly as it would after a passkey. A code is good " +
                 "once, for ten minutes, for the address it was sent to, and for five attempts. " +
                 "Every failure — unknown address, wrong digits, expired, already spent, " +
                 "attempts exhausted, locked-out account — is the same 401. The resulting " +
                 "session may reach the account area and may not use a Contributor or " +
                 "Administrator role; that needs a passkey or an authenticator code.")
             .Produces<SignInResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
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

        // Requires a real session rather than an enrolment ticket. A ticket is
        // permission to add a credential after proving mailbox control; if it
        // also removed them, an intercepted recovery link would be a way to
        // strip an account of every credential it already had.
        group.MapDelete("/passkey/{credentialId}", PasskeyHandlers.RemoveAsync)
             .WithName("removePasskey")
             .WithSummary("Remove a passkey from the account.")
             .WithDescription(
                 "Revokes one of the signed-in account's credentials, named by its base64url " +
                 "credential id. Refuses to remove the last remaining passkey, because passkeys " +
                 "are the only credential this platform issues and removing the last one would " +
                 "strand the account rather than secure it. The account is emailed about the " +
                 "change.")
             .Produces<PasskeyRemovedResponse>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

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

    /// <summary>
    /// Raising a session that already exists to a stronger one.
    /// </summary>
    /// <remarks>
    /// Every route here requires a session and none of them can create one, so
    /// none is a way into an account. See <see cref="ReauthenticationHandlers"/>
    /// for why adding the claim later is not a weakening of the rule that a
    /// session records how it was established.
    /// </remarks>
    private static void MapReauthentication(RouteGroupBuilder group)
    {
        group.MapPost("/reauthenticate/passkey/begin", ReauthenticationHandlers.BeginPasskeyAsync)
             .WithName("beginReauthentication")
             .WithSummary("Start proving a passkey on the current session.")
             .WithDescription(
                 "Returns WebAuthn request options for navigator.credentials.get(), naming the " +
                 "signed-in account's own credentials so the browser offers those and no others. " +
                 "Unlike the sign-in ceremony this one identifies the account, because the caller " +
                 "is already signed in and there is nothing left to keep from them.")
             .Produces<object>(contentType: "application/json")
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapPost("/reauthenticate/passkey/complete", ReauthenticationHandlers.CompletePasskeyAsync)
             .WithName("completeReauthentication")
             .WithSummary("Finish proving a passkey on the current session.")
             .WithDescription(
                 "Verifies the assertion, refuses it if the credential belongs to any account " +
                 "other than the one signed in, and re-issues the session cookie stamped as a " +
                 "passkey sign-in. Answers with the same body a sign-in does.")
             .Produces<SignInResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
             .RequireRateLimiting(AuthRateLimiting.SensitivePolicy);

        group.MapPost("/reauthenticate/totp", ReauthenticationHandlers.CompleteTotpAsync)
             .WithName("reauthenticateWithTotp")
             .WithSummary("Prove an authenticator code on the current session.")
             .WithDescription(
                 "For an account that already has an authenticator app. Verifies a six-digit code " +
                 "and re-issues the session cookie stamped as an authenticator sign-in. A wrong " +
                 "code counts against the account's lockout exactly as a wrong code at sign-in " +
                 "does, so this is not an unmetered guessing oracle for a stolen session.")
             .Produces<SignInResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.SignedIn)
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

    /// <summary>
    /// The administrative routes: the account directory, the role grant, the
    /// suspension switch, deletion, and the log of all four.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every route below requires <see cref="Sw5ePolicies.Administer"/>, which
    /// is the Administrator role <em>and</em> a session established with a
    /// passkey or an authenticator app. Both halves are load-bearing and
    /// neither is inherited from the group: an emailed code proves control of a
    /// mailbox, and a mailbox is what everything else on the internet is
    /// recovered through, so it can open somebody's own account and can never
    /// open a list of everybody else's addresses.
    /// </para>
    /// <para>
    /// <b>The listing is the reason the rest of this exists.</b> Before it, the
    /// role grant below was addressed by an account identifier that nothing in
    /// the API would tell anybody — no listing, no search, no lookup by
    /// address. The single administrative capability the platform had was
    /// therefore reachable only from a database client, which is another way of
    /// saying it was not reachable.
    /// </para>
    /// <para>
    /// The refusals are worth reading as carefully as the permissions. An
    /// anonymous caller gets the cookie handler's 401 and a caller with the
    /// wrong role gets its 403, both written before any handler runs, both
    /// identical whether the account they named exists or not, and neither
    /// costing a single query. That is what keeps this from being an
    /// enumeration oracle: there is no path from a non-administrator's request
    /// to a database read, so there is nothing for a response shape or a
    /// response time to differ on.
    /// </para>
    /// </remarks>
    private static void MapAdministration(RouteGroupBuilder group)
    {
        group.MapGet("/admin/users", UserDirectoryHandlers.ListAsync)
             .WithName("listUsers")
             .WithSummary("Find an account.")
             .WithDescription(
                 "Administrators only. The account directory, oldest first, paginated. `q` " +
                 "matches an email address or a display name, case-insensitively, and needs at " +
                 "least two characters — below that it is a table dump wearing a search box. " +
                 "`role` filters to one of Community, Contributor or Administrator; `status` to " +
                 "active, suspended, unverified or all. An unrecognised filter value is a 400 " +
                 "rather than no filter, because showing somebody the whole directory while " +
                 "they believe they are looking at one slice of it is how the wrong account " +
                 "gets acted on. This response carries email addresses and is the only one on " +
                 "the platform that carries anybody else's.")
             .Produces<AdminUserListResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapGet("/admin/users/{userId:guid}", UserDirectoryHandlers.GetAsync)
             .WithName("getUser")
             .WithSummary("One account, in administrative detail.")
             .WithDescription(
                 "Administrators only. Everything the directory shows for one account, plus how " +
                 "many unpublished drafts it owns — which is the one thing that will refuse a " +
                 "deletion, and is therefore worth knowing before trying one. The draft count " +
                 "is null on a deployment that serves content from files and has no authoring " +
                 "at all.")
             .Produces<AdminUserDetail>()
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapPut("/admin/users/{userId:guid}/suspension", AccountLifecycleHandlers.SetSuspensionAsync)
             .WithName("setAccountSuspension")
             .WithSummary("Suspend an account, or reinstate it.")
             .WithDescription(
                 "Administrators only, and declarative like the role grant: send suspended=true " +
                 "with a reason, or suspended=false to lift it. A suspended account cannot sign " +
                 "in by any route, and its open sessions end on their very next request rather " +
                 "than when the cookie expires. Its passkeys are left in place and are inert " +
                 "while the suspension stands, so reinstating restores access rather than " +
                 "requiring the account to be credentialled again. The reason is required when " +
                 "suspending, is written for the other administrators, and is never shown to " +
                 "the account — which is told that it has been suspended and who to write to. " +
                 "An administrator cannot suspend themselves.")
             .Produces<AccountSuspensionStateResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapDelete("/admin/users/{userId:guid}", AccountLifecycleHandlers.DeleteAsync)
             .WithName("deleteUser")
             .WithSummary("Delete an account.")
             .WithDescription(
                 "Administrators only, and not reversible. Removes the account and everything " +
                 "that identifies it: the address, the display name, the roles, the passkeys, " +
                 "the authenticator secret and any live sign-in code. It does not remove what " +
                 "the account wrote — content revisions and moderation reports keep their " +
                 "identifier and afterwards render as a removed account, because a history that " +
                 "can be edited by deleting an account is not a history. Refused while the " +
                 "account owns unpublished drafts: publish or discard those first. An " +
                 "administrator cannot delete themselves.")
             .Produces<AccountDeletedResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

        group.MapGet("/admin/audit", UserDirectoryHandlers.ListActionsAsync)
             .WithName("listAdministrativeActions")
             .WithSummary("What administrators have done.")
             .WithDescription(
                 "Administrators only. Every role change, suspension, reinstatement and " +
                 "deletion, newest first, filterable by subject, by actor and by action. The " +
                 "display names are copies taken at the time, so an entry stays readable after " +
                 "either account has gone — which is the whole point of the one entry that " +
                 "records a deletion. The table is append-only at the database: nothing in this " +
                 "API updates or removes a row, and PostgreSQL refuses to as well.")
             .Produces<AdministrativeLogResponse>()
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status429TooManyRequests)
             .RequireAuthorization(Sw5ePolicies.Administer)
             .RequireRateLimiting(AuthRateLimiting.StandardPolicy);

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
