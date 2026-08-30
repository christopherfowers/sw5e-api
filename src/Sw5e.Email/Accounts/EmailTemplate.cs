using System.Net;
using System.Text.RegularExpressions;

namespace Sw5e.Email.Accounts;

/// <summary>
/// The smallest thing that can fill placeholders in a template without being a
/// security hole.
/// </summary>
/// <remarks>
/// <para>
/// A templating engine is not wanted here. The templates are a fixed, small set
/// of transactional messages checked in beside this file; they need no loops,
/// no conditionals and no partials, and every one of those features is a way
/// for a template to do something surprising with a value that came from a
/// user. What is wanted is substitution that cannot be tricked into emitting
/// markup.
/// </para>
/// <para>
/// Two rules make that true, and they are the entire reason this type exists
/// rather than a chain of <c>string.Replace</c> calls:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Values are encoded for their destination.</b> The HTML part encodes
///     every substituted value; the plain-text part encodes none. A display
///     name is whatever a user typed into a registration form, and
///     <c>&lt;script&gt;</c> in a display name must reach the reader as visible
///     text, not as a tag. Encoding also makes an <c>&amp;</c> in a signed URL
///     survive as <c>&amp;amp;</c> inside an <c>href</c>, which is what makes
///     multi-parameter reset links actually work.
///   </description></item>
///   <item><description>
///     <b>A placeholder with no value is a failure, not an empty string.</b>
///     Silent substitution turns a renamed placeholder into an email that
///     reads "click here to reset your password:" with nothing after the colon,
///     and it turns a typo into <c>{{ActionUrl}}</c> printed verbatim to a
///     locked-out user. Both are worse than not sending. Throwing makes the
///     mistake a failing test instead.
///   </description></item>
/// </list>
/// </remarks>
internal static partial class EmailTemplate
{
    /// <summary>
    /// Substitutes every <c>{{Name}}</c> in <paramref name="template"/>.
    /// </summary>
    /// <param name="template">
    /// A trusted literal from <see cref="AccountEmailTemplates"/>. Never a
    /// value that came from outside the process — the whole encoding scheme
    /// below assumes the template itself is the safe part.
    /// </param>
    /// <param name="values">The value for each placeholder name.</param>
    /// <param name="htmlEncode">
    /// True when rendering the <c>text/html</c> part, false for
    /// <c>text/plain</c>. Getting this backwards is visible immediately: the
    /// text part fills with <c>&amp;amp;</c>, or the HTML part renders a user's
    /// markup.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The template contains a placeholder that <paramref name="values"/> has
    /// no entry for.
    /// </exception>
    public static string Render(
        string template,
        IReadOnlyDictionary<string, string> values,
        bool htmlEncode)
    {
        return Placeholder().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;

            if (!values.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException(
                    $"The email template references the placeholder '{name}', " +
                    "which was not supplied. Every placeholder must have a value; " +
                    "an email with a hole in it is not worth sending.");
            }

            // WebUtility rather than HttpUtility: this library has no ASP.NET
            // dependency, and WebUtility.HtmlEncode escapes the five characters
            // that matter (& < > " ') which covers both element content and
            // quoted attribute values — the only two places a value is ever
            // substituted in these templates.
            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
    }

    /// <summary>
    /// Matches <c>{{Name}}</c>, allowing incidental whitespace inside the
    /// braces so that a stray space in a template is a substitution rather
    /// than a literal that ships to a user.
    /// </summary>
    [GeneratedRegex(
        @"\{\{\s*(?<name>[A-Za-z][A-Za-z0-9]*)\s*\}\}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Placeholder();
}
