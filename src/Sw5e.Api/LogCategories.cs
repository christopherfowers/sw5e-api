namespace Sw5e.Api;

/// <summary>
/// The names this API logs under.
/// </summary>
/// <remarks>
/// <para>
/// These were string literals, repeated eighteen times across nine files, and
/// a category is not a cosmetic thing to get wrong. It is what an operator
/// filters on: <c>Logging:LogLevel:Sw5e.Api.Accounts</c> in configuration
/// turns the account routes up or down without touching anything else, and a
/// single mistyped literal quietly moves one file's output out of that filter
/// — where it is neither raised with the rest nor silenced with the rest, and
/// nobody notices until the day somebody is reading logs in a hurry.
/// </para>
/// <para>
/// Written as constants rather than as <c>ILogger&lt;T&gt;</c> because these
/// handlers are static classes with no instance to inject into, and because
/// the category is deliberately per-feature rather than per-class: an operator
/// raising the account routes wants all of them, not
/// <c>Sw5e.Api.Features.Accounts.PasskeyHandlers</c> and eleven siblings.
/// </para>
/// </remarks>
internal static class LogCategories
{
    /// <summary>
    /// Registration, verification, sign-in, second factors, and the
    /// administrative account routes.
    /// </summary>
    /// <remarks>
    /// One place outside this assembly writes under this name as well —
    /// <c>Sw5e.Identity.Administration.AccountSuspension</c>, which ends a
    /// live session when an account is suspended and belongs in the same
    /// stream as the routes that suspended it. It cannot reference this
    /// constant, because the dependency runs the other way, so it carries the
    /// literal and a comment pointing here.
    /// </remarks>
    public const string Accounts = "Sw5e.Api.Accounts";

    /// <summary>Drafting, publishing and reverting content.</summary>
    public const string Authoring = "Sw5e.Api.Authoring";

    /// <summary>Reports raised against content, and their triage.</summary>
    public const string Moderation = "Sw5e.Api.Moderation";

    /// <summary>Reading the published content set.</summary>
    public const string Content = "Sw5e.Api.Content";
}
