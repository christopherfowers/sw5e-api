using System.Text;
using System.Text.RegularExpressions;

namespace Sw5e.Email.Tests.Support;

/// <summary>One decoded body part.</summary>
/// <param name="ContentType">The part's <c>Content-Type</c> header, verbatim.</param>
/// <param name="Body">The part's content, transfer-decoding undone.</param>
internal sealed record MimePart(string ContentType, string Body)
{
    /// <summary>The media type without its parameters, lowercased.</summary>
    public string MediaType =>
        ContentType.Split(';')[0].Trim().ToLowerInvariant();
}

/// <summary>
/// Just enough of RFC 5322 and RFC 2045 to assert on what came off the wire.
/// </summary>
/// <remarks>
/// <para>
/// The framework ships no MIME parser, and the test needs one for a real
/// reason: the adapter's output is a <c>multipart/alternative</c> whose parts
/// are quoted-printable, so a naive "does the raw DATA contain the reset link"
/// assertion would fail the moment quoted-printable inserted a soft line break
/// into the middle of a long URL — which for a signed token URL is every time.
/// </para>
/// <para>
/// Decoding it here means the assertions are about what a mail client would
/// actually show a reader, which is the property that matters, and it doubles
/// as a check that the message is well-formed enough to be decoded at all.
/// </para>
/// </remarks>
internal static partial class MimeMessage
{
    /// <summary>A parsed message.</summary>
    /// <param name="Headers">Top-level headers, unfolded, keyed case-insensitively.</param>
    /// <param name="Parts">
    /// The decoded body parts. A message with no multipart structure yields
    /// one part.
    /// </param>
    public sealed record Parsed(IReadOnlyDictionary<string, string> Headers, IReadOnlyList<MimePart> Parts)
    {
        /// <summary>The single part of the given media type.</summary>
        public MimePart Part(string mediaType) =>
            Parts.SingleOrDefault(part => part.MediaType == mediaType)
            ?? throw new InvalidOperationException(
                $"The message has no single {mediaType} part. It has: " +
                string.Join(", ", Parts.Select(part => part.MediaType)));

        /// <summary>A header value, with any RFC 2047 encoded words decoded.</summary>
        public string Header(string name) =>
            Headers.TryGetValue(name, out var value)
                ? DecodeEncodedWords(value)
                : throw new InvalidOperationException(
                    $"The message has no {name} header. It has: " +
                    string.Join(", ", Headers.Keys));
    }

    public static Parsed Parse(string raw)
    {
        var (headers, body) = SplitHeaders(raw);

        if (!headers.TryGetValue("Content-Type", out var contentType) ||
            !contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            var encoding = headers.GetValueOrDefault("Content-Transfer-Encoding", "7bit");
            return new Parsed(
                headers,
                [new MimePart(contentType ?? "text/plain", Decode(body, encoding))]);
        }

        var boundary = BoundaryPattern().Match(contentType) is { Success: true } match
            ? match.Groups["boundary"].Value.Trim('"')
            : throw new InvalidOperationException(
                $"A multipart Content-Type carried no boundary parameter: {contentType}");

        var parts = new List<MimePart>();

        // Splitting on the delimiter is enough here because the fixture only
        // ever handles messages this library produced, which are two flat text
        // parts with no nesting.
        foreach (var chunk in body.Split("--" + boundary))
        {
            var trimmed = chunk.Trim('\r', '\n');

            if (trimmed.Length == 0 || trimmed == "--")
            {
                // The preamble before the first delimiter and the closing
                // "--boundary--" epilogue.
                continue;
            }

            var (partHeaders, partBody) = SplitHeaders(chunk.TrimStart('\r', '\n'));

            parts.Add(new MimePart(
                partHeaders.GetValueOrDefault("Content-Type", "text/plain"),
                Decode(partBody, partHeaders.GetValueOrDefault("Content-Transfer-Encoding", "7bit"))));
        }

        return new Parsed(headers, parts);
    }

    /// <summary>
    /// Splits a header block from a body and unfolds continuation lines.
    /// </summary>
    /// <remarks>
    /// Unfolding matters: a long header is wrapped onto continuation lines
    /// beginning with whitespace, and a Subject or From header that is a
    /// realistic length will be wrapped. Without unfolding, an assertion on
    /// the full value fails for formatting reasons rather than for real ones.
    /// </remarks>
    private static (Dictionary<string, string> Headers, string Body) SplitHeaders(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = raw.Split("\r\n");
        var index = 0;
        string? currentName = null;
        var currentValue = new StringBuilder();

        void Commit()
        {
            if (currentName is not null)
            {
                headers[currentName] = currentValue.ToString();
            }

            currentName = null;
            currentValue.Clear();
        }

        for (; index < lines.Length; index++)
        {
            var line = lines[index];

            if (line.Length == 0)
            {
                index++;
                break;
            }

            if (line[0] is ' ' or '\t')
            {
                currentValue.Append(line.TrimStart());
                continue;
            }

            Commit();

            var separator = line.IndexOf(':', StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            currentName = line[..separator];
            currentValue.Append(line[(separator + 1)..].TrimStart());
        }

        Commit();

        return (headers, string.Join("\r\n", lines.Skip(index)));
    }

    private static string Decode(string body, string transferEncoding) =>
        transferEncoding.Trim().ToLowerInvariant() switch
        {
            "quoted-printable" => DecodeQuotedPrintable(body),
            "base64" => Encoding.UTF8.GetString(
                Convert.FromBase64String(body.Replace("\r\n", string.Empty, StringComparison.Ordinal))),
            _ => body,
        };

    /// <summary>Undoes RFC 2045 quoted-printable.</summary>
    private static string DecodeQuotedPrintable(string body)
    {
        var bytes = new List<byte>(body.Length);

        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '=')
            {
                bytes.Add((byte)body[i]);
                continue;
            }

            // "=" at end of line is a soft break inserted purely to keep lines
            // under 76 characters. It is not part of the content.
            if (i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n')
            {
                i += 2;
                continue;
            }

            if (i + 2 < body.Length &&
                byte.TryParse(body.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                bytes.Add(value);
                i += 2;
                continue;
            }

            bytes.Add((byte)body[i]);
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>
    /// Decodes RFC 2047 encoded words, so a header assertion reads the value a
    /// human would see rather than <c>=?utf-8?B?…?=</c>.
    /// </summary>
    private static string DecodeEncodedWords(string value) =>
        EncodedWordPattern().Replace(value, match =>
        {
            var charset = Encoding.GetEncoding(match.Groups["charset"].Value);
            var content = match.Groups["content"].Value;

            return match.Groups["encoding"].Value.ToUpperInvariant() switch
            {
                "B" => charset.GetString(Convert.FromBase64String(content)),
                // Q encoding is quoted-printable with underscore standing in
                // for a space.
                "Q" => DecodeQuotedPrintable(content.Replace('_', ' ')),
                _ => match.Value,
            };
        });

    [GeneratedRegex(@"boundary=(?<boundary>""[^""]+""|[^\s;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BoundaryPattern();

    [GeneratedRegex(@"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<content>[^?]*)\?=")]
    private static partial Regex EncodedWordPattern();
}
