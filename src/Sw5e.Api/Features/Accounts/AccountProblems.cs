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
    /// A re-authentication ceremony could not be completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="PasskeyFailed"/> only because that one says
    /// "could not be registered", which is the wrong sentence to show somebody
    /// who was confirming a credential they already hold.
    /// </para>
    /// <para>
    /// One answer for every cause — no challenge in flight, an expired one, a
    /// signature that did not verify, and a credential belonging to a different
    /// account. The last of those is the interesting one, and it is deliberately
    /// not distinguished: telling a caller that their credential is valid but
    /// attached to somebody else is telling them something about somebody else.
    /// </para>
    /// </remarks>
    public static ProblemHttpResult ReauthenticationFailed => TypedResults.Problem(
        title: "Confirmation failed",
        detail: "That could not be confirmed. Start again.",
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

    /// <summary>
    /// An administrator tried to suspend or delete their own account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same reasoning that already refuses self-demotion, applied to the
    /// two other ways of reaching the same end state. The administrator role is
    /// the only thing that can grant the administrator role, so any action that
    /// takes the last one out of circulation leaves the platform with no way to
    /// appoint another short of editing the database by hand — and combined
    /// with the self-demotion rule, refusing every self-directed removal is
    /// what makes "the set of administrators can never reach zero through this
    /// API" a property rather than a hope.
    /// </para>
    /// <para>
    /// It is also the move most attractive to somebody who has just stolen an
    /// administrator's session, and the one the real administrator would have
    /// the hardest time undoing.
    /// </para>
    /// </remarks>
    public static ProblemHttpResult NotOnYourself(string action) => TypedResults.Problem(
        title: "Not on your own account",
        detail:
            $"An administrator cannot {action} their own account. Ask another " +
            "administrator to do it.",
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// The account still owns unpublished drafts, so deleting it would strand
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A draft is not history — it is a proposal somebody has not finished, and
    /// it holds the only editing slot for the document it names. Deleting its
    /// author and leaving it behind would leave a draft attributed to nobody
    /// blocking everybody else from editing that entry; deleting it silently
    /// would throw away work whose value the deleting administrator has no way
    /// to judge.
    /// </para>
    /// <para>
    /// So the deletion is refused and says how many, and the administrator
    /// publishes or discards them through the authoring surface that owns them
    /// first. That also keeps this endpoint from writing to the content schema
    /// at all: identity deletion touches one database, in one transaction, and
    /// has no half-done state.
    /// </para>
    /// </remarks>
    public static ProblemHttpResult DraftsOutstanding(int count) => TypedResults.Problem(
        title: "Drafts outstanding",
        detail:
            $"That account owns {count} unpublished " +
            (count == 1 ? "draft" : "drafts") +
            ". Publish or discard them first — deleting the account would leave the work " +
            "attributed to nobody and would keep anyone else from editing those entries.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = "drafts-outstanding",
            ["draftCount"] = count,
        });
}
