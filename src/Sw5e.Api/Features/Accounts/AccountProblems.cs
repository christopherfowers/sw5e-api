using Microsoft.AspNetCore.Http.HttpResults;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// The refusals the account endpoints are allowed to give.
/// </summary>
/// <remarks>
/// <para>
/// A short, fixed list, and that is the point. Error messages are an
/// information channel: an endpoint that distinguishes "no such account" from
/// "wrong code" from "account locked" has told an attacker three separate
/// facts, and every one of them makes the next attempt cheaper. Every
/// authentication failure in this API therefore collapses to the same wording
/// and the same status.
/// </para>
/// <para>
/// The cost lands on support rather than on security, and it is bounded: the
/// server log distinguishes these cases in full, keyed by account identifier,
/// so an operator can always answer what the caller could not be told.
/// </para>
/// <para>
/// Each of these builds a fresh result rather than handing out a cached one.
/// The problem details middleware decorates the object it is given — it stamps
/// a trace identifier onto it, among other things — so a single shared instance
/// would be mutated by every request that returned it, and two concurrent
/// refusals could hand each other's correlation data to the wrong caller.
/// </para>
/// </remarks>
internal static class AccountProblems
{
    /// <summary>
    /// Every way a sign-in can fail. Unknown credential, invalid signature,
    /// expired challenge, unverified address, locked-out account, disabled
    /// account: one answer for all of them.
    /// </summary>
    /// <remarks>
    /// Locked-out deserves a note, because reporting it is a common and
    /// well-intentioned mistake. "This account is locked" confirms the account
    /// exists, and it also confirms to somebody running a lockout attack that
    /// their denial of service is working, which is exactly the feedback that
    /// makes it worth continuing.
    /// </remarks>
    public static ProblemHttpResult SignInFailed => TypedResults.Problem(
        title: "Sign-in failed",
        detail: "That sign-in attempt could not be completed.",
        statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The caller has neither a session nor any half-finished flow that would
    /// entitle them to the endpoint.
    /// </summary>
    public static ProblemHttpResult NotAuthenticated => TypedResults.Problem(
        title: "Authentication required",
        detail: "This request requires a signed-in account.",
        statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// An email verification or recovery token was missing, malformed, expired,
    /// already spent, or issued for an address with no account.
    /// </summary>
    public static ProblemHttpResult VerificationFailed => TypedResults.Problem(
        title: "Verification failed",
        detail: "That link is invalid or has expired. Request a new one.",
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// A passkey ceremony could not be completed: no challenge in flight, a
    /// challenge that expired, or a credential that failed verification.
    /// </summary>
    public static ProblemHttpResult PasskeyFailed => TypedResults.Problem(
        title: "Passkey registration failed",
        detail: "That passkey could not be registered. Start again.",
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// The request body did not describe something the endpoint could act on.
    /// </summary>
    /// <remarks>
    /// Safe to be specific here, and useful to be: the shape of a request is
    /// public knowledge, documented in the OpenAPI document, and says nothing
    /// about any account. What must never appear in one of these is a value
    /// read out of the store.
    /// </remarks>
    public static ProblemHttpResult Invalid(string detail) => TypedResults.Problem(
        title: "Invalid request",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// The caller asked to remove a credential that is not on their account.
    /// </summary>
    /// <remarks>
    /// Safe to distinguish, because the route already requires a session and
    /// the only credentials it can speak about are the caller's own. It tells
    /// an account holder nothing they could not learn from
    /// <c>GET /api/auth/me</c>, and it is the difference between a page that
    /// says "already gone" and one that silently claims success.
    /// </remarks>
    public static ProblemHttpResult NoSuchCredential => TypedResults.Problem(
        title: "No such passkey",
        detail: "That passkey is not registered to this account.",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The caller asked to remove the only credential their account has.
    /// </summary>
    /// <remarks>
    /// Refused rather than obeyed. Passkeys are the only credential this
    /// platform issues, so removing the last one does not lock the account
    /// down, it strands it: nobody — including the owner — can sign in again,
    /// and the only way back is the recovery email, which re-credentials the
    /// account rather than restoring it. A reader who genuinely wants to stop
    /// using a device enrols the replacement first, which is what the
    /// <c>code</c> below lets the front end say.
    /// </remarks>
    public static ProblemHttpResult LastCredential => TypedResults.Problem(
        title: "Last passkey",
        detail:
            "That is the only passkey on this account. Add another one first, " +
            "or the account would have no way to sign in.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?> { ["code"] = "last-credential" });

    /// <summary>The named account does not exist. Administrative routes only.</summary>
    /// <remarks>
    /// Distinguishable from success on purpose, and safe because every route
    /// that can produce it already requires the administrator role. Withholding
    /// it would only stop an administrator finding out that the identifier they
    /// were given is wrong.
    /// </remarks>
    public static ProblemHttpResult NoSuchAccount => TypedResults.Problem(
        title: "No such account",
        detail: "No account with that identifier exists.",
        statusCode: StatusCodes.Status404NotFound);
}
