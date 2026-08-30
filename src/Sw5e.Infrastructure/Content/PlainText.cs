using System.Text;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// Markdown-to-plain-text reduction, good enough for a summary line, a search
/// blob and a match snippet.
/// </summary>
/// <remarks>
/// Written as a single linear pass rather than a set of regular expressions.
/// The input is content-authored Markdown of unbounded length, and a
/// backtracking pattern over it is a denial-of-service waiting to be indexed.
/// </remarks>
internal static class PlainText
{
    /// <summary>
    /// Strips the Markdown punctuation that would otherwise show up in a
    /// summary line, keeps link labels, and collapses runs of whitespace.
    /// </summary>
    public static string Flatten(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(markdown.Length);
        var lastWasSpace = false;

        for (var i = 0; i < markdown.Length; i++)
        {
            var c = markdown[i];

            // Link syntax: keep the label, drop the target.
            if (c == '[')
            {
                var close = markdown.IndexOf(']', i + 1);

                if (close > i && close + 1 < markdown.Length && markdown[close + 1] == '(')
                {
                    var end = markdown.IndexOf(')', close + 2);

                    if (end > close)
                    {
                        builder.Append(markdown, i + 1, close - i - 1);
                        i = end;
                        lastWasSpace = false;
                        continue;
                    }
                }

                continue;
            }

            if (c is '*' or '_' or '#' or '`' or ']' or '>' or '|')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>Cuts <paramref name="text"/> to length on a word boundary where one is near.</summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));

        if (cut < maxLength / 2)
        {
            cut = maxLength;
        }

        return text.AsSpan(0, cut).TrimEnd().ToString() + Ellipsis;
    }

    /// <summary>
    /// A window of text around a match, elided at either end when it does not
    /// start or finish at the boundary of the field.
    /// </summary>
    public static string Snippet(string text, int matchIndex, int matchLength, int window)
    {
        var start = Math.Max(0, matchIndex - window);
        var end = Math.Min(text.Length, matchIndex + matchLength + window);

        var builder = new StringBuilder();

        if (start > 0)
        {
            builder.Append(Ellipsis);
        }

        builder.Append(text.AsSpan(start, end - start).Trim());

        if (end < text.Length)
        {
            builder.Append(Ellipsis);
        }

        return builder.ToString();
    }

    private const string Ellipsis = "…";
}
