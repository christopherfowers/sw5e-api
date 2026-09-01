using Microsoft.AspNetCore.Http.HttpResults;

namespace Sw5e.Api.Features.Moderation;

/// <summary>
/// The refusals the flag endpoints produce.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than composed inline, so that the whole set can be read
/// at once and so that a refusal cannot accidentally start saying something
/// different from the one beside it.
/// </para>
/// <para>
/// These are noticeably more forthcoming than the account API's, and that is a
/// judgement rather than an inconsistency. Every refusal there is shaped by
/// account enumeration: saying <em>why</em> a sign-in failed tells an attacker
/// which half of the guess was right. Nothing here is a credential. A caller
/// already holds a session, the content registry is published in full, and the
/// worst thing an over-specific message could confirm is whether a document
/// this site openly serves exists. Vagueness would buy nothing and would cost
/// the reporter the chance to fix their report.
/// </para>
/// <para>
/// The one thing these must never contain is text that came from a request.
/// Echoing a rejected value back into a message is how a problem document ends
/// up carrying somebody else's payload.
/// </para>
/// </remarks>
internal static class FlagProblems
{
    /// <summary>
    /// A field of the request was missing or would not parse.
    /// </summary>
    /// <remarks>
    /// Carries <c>fieldErrors</c> as well as <c>detail</c>, because the browser
    /// client puts the message beside the control that produced it rather than
    /// at the top of the form, and a form that says "something is wrong"
    /// without saying where is a form people abandon.
    /// </remarks>
    public static ProblemHttpResult Invalid(string field, string detail) =>
        TypedResults.Problem(
            title: "That report could not be filed",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["fieldErrors"] = new Dictionary<string, string> { [field] = detail },
            });

    /// <summary>The request body was absent or was not an object.</summary>
    public static ProblemHttpResult MissingBody =>
        TypedResults.Problem(
            title: "That report could not be filed",
            detail: "The request carried no report.",
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// The target named does not exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check behind this is the reason the endpoint reads the content store
    /// at all, and it is not tidiness. A flag whose target does not exist is
    /// worse than no flag: it cannot be reviewed, because there is nothing to
    /// look at; it cannot be closed, because nobody can tell whether it was
    /// real; and a queue that accepts them is a queue an anonymous-shaped
    /// attacker can fill with rows that will never leave it.
    /// </para>
    /// <para>
    /// 404 rather than 400 because the request was well-formed and named
    /// something that is simply not here — which is also the answer
    /// <c>/api/content/{type}/{key}</c> gives for the same key, so the two
    /// cannot disagree about what exists.
    /// </para>
    /// </remarks>
    public static ProblemHttpResult NoSuchTarget =>
        TypedResults.Problem(
            title: "Nothing to report",
            detail: "This site does not publish anything with that key, so there is nothing " +
                    "for a report to point at.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>The caller already has this exact report outstanding.</summary>
    /// <remarks>
    /// A conflict rather than a silent success. Answering 201 to a resubmission
    /// would tell the reporter they had filed a second report, and would make a
    /// double-click indistinguishable from a deliberate second report to
    /// everybody involved.
    /// </remarks>
    public static ProblemHttpResult AlreadyReported =>
        TypedResults.Problem(
            title: "You have already reported this",
            detail: "You have an open report of the same kind against this. It has not been " +
                    "lost — it is waiting for a reviewer.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = "duplicate-report" });

    /// <summary>The account has filed as much as it may for now.</summary>
    /// <remarks>
    /// <para>
    /// 429, and deliberately the same status the per-address limiter uses, so a
    /// client has one branch for "you are going too fast" rather than two.
    /// </para>
    /// <para>
    /// The message says which limit was reached because it is the reporter's
    /// own account and they can do nothing about it without knowing. It does
    /// not say what the number is; a limit whose exact value is published is a
    /// limit somebody sits exactly under.
    /// </para>
    /// </remarks>
    public static ProblemHttpResult QuotaReached(string detail) =>
        TypedResults.Problem(
            title: "Too many reports",
            detail: detail,
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?> { ["code"] = "report-quota" });

    /// <summary>No flag with that identifier exists.</summary>
    public static ProblemHttpResult NoSuchFlag =>
        TypedResults.Problem(
            title: "No such report",
            detail: "No report with that identifier exists.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// The lifecycle does not allow that move.
    /// </summary>
    /// <remarks>
    /// Includes what the flag is actually in, because the overwhelmingly likely
    /// cause is two reviewers working the same queue and one of them acting on
    /// a page that is a minute old. Telling them where it got to is the
    /// difference between that and a page that appears broken.
    /// </remarks>
    public static ProblemHttpResult BadTransition(string from, string to) =>
        TypedResults.Problem(
            title: "That is not a move this report can make",
            detail: $"This report is {from}, and {from} to {to} is not a transition the queue " +
                    "allows. Somebody may have acted on it since this page was loaded.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "invalid-transition",
                ["status"] = from,
            });

    /// <summary>
    /// The route required a session and the principal turned out to have no
    /// account behind it.
    /// </summary>
    /// <remarks>
    /// Reachable in one window: a cookie that outlives a deleted account, until
    /// the security stamp validator next runs. The right answer is that there
    /// is nobody here.
    /// </remarks>
    public static ProblemHttpResult NotAuthenticated =>
        TypedResults.Problem(
            title: "Authentication required",
            detail: "This request requires a signed-in account.",
            statusCode: StatusCodes.Status401Unauthorized);
}
