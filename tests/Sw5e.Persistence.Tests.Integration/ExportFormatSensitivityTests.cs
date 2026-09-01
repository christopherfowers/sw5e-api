using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Shouldly;
using Sw5e.Database.Schemas;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The round-trip assertion would notice a writer that changed member order or
/// formatting.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ContentCorpusRoundTripTests"/> compares two strings and requires
/// them to be equal. On its own that establishes only that the two sides agree
/// today — not that the comparison could ever have caught them disagreeing, and
/// not which parts of the format it actually pins. A test suite where the only
/// evidence is a passing equality is a suite that would keep passing if the
/// exporter and the corpus drifted together.
/// </para>
/// <para>
/// So each case below is a plausible way the writer could have been wrong —
/// members in the order <c>jsonb</c> hands them back, four-space indentation,
/// no indentation, CRLF, no trailing newline, non-ASCII escaped — rendered from
/// real documents and required to produce something the round trip would
/// reject. If a mutation slipped through, the exporter could change that thing
/// freely and the suite would stay green.
/// </para>
/// <para>
/// No database: this is about the writer and the committed bytes, and needing a
/// container to establish it would only make it slower to run.
/// </para>
/// </remarks>
public sealed class ExportFormatSensitivityTests
{
    /// <summary>
    /// Documents to mutate, one per shape worth worrying about.
    /// </summary>
    /// <remarks>
    /// Named rather than sampled. A negative control that picks different files
    /// on each run is a negative control that fails on somebody else's machine.
    /// These three are chosen for what they contain: a species with nested
    /// objects, arrays of objects and prose; a stat block with two dozen
    /// members, numbers and a deep behaviour list; and several kilobytes of
    /// markdown. All three carry non-ASCII characters, which the last mutation
    /// needs to have anything to change.
    /// </remarks>
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();

        foreach (var mutation in Mutations)
        {
            foreach (var document in Documents)
            {
                data.Add(mutation, document);
            }
        }

        return data;
    }

    private static readonly string[] Documents =
    [
        "species/wookiee",
        "monster/000-series-protocol-droid",
        "rule/combination-weapons",
    ];

    private static readonly string[] Mutations =
    [
        "jsonb member order",
        "four-space indentation",
        "no indentation",
        "CRLF line endings",
        "no trailing newline",
        "non-ASCII escaped",
    ];

    /// <summary>
    /// The types whose documents are short enough that the schema's order and
    /// the database's happen to agree.
    /// </summary>
    /// <remarks>
    /// A four-member glossary entry — key, name, contentSet, description — is
    /// already in length-then-bytes order, and so are a lightsaber form and a
    /// starship venture. Listed rather than counted so that the day a large
    /// type joins them, this says so instead of quietly widening.
    /// </remarks>
    private static readonly string[] CoincidentalTypes =
        ["armor-property", "weapon-property", "lightsaber-form", "starship-venture"];

    private static readonly CanonicalContent Canonical =
        new(new SchemaRepository(ContentFixture.SchemaPath));

    [Theory]
    [MemberData(nameof(Cases))]
    public void AWriterThatGotThisWrongWouldNotReproduceTheCommittedFile(
        string mutation,
        string document)
    {
        var contentType = document[..document.IndexOf('/', StringComparison.Ordinal)];
        var path = Path.Combine(
            ContentFixture.CommittedCorpus,
            contentType,
            document[(contentType.Length + 1)..] + ".json");

        File.Exists(path).ShouldBeTrue(
            $"'{document}' is named by this test and is not in the corpus. Pick another " +
            "document of the same shape rather than deleting the case.");

        var committed = File.ReadAllText(path, Encoding.UTF8)
                            .Replace("\r\n", "\n", StringComparison.Ordinal);

        using var parsed = JsonDocument.Parse(committed);

        // The anchor. If the canonical rendering is not already the committed
        // file, the mutation below proves nothing about anything.
        Canonical.Render(contentType, parsed.RootElement).ShouldBe(committed, document);

        Mutate(mutation, contentType, parsed.RootElement)
            .ShouldNotBe(
                committed,
                $"a writer producing '{mutation}' would still have reproduced {document}, which " +
                "means the round-trip assertion does not pin that part of the format");
    }

    /// <summary>
    /// Ordering members the way the database returns them is not a hypothetical
    /// mistake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is precisely what the exporter produces if it writes a document out
    /// the way it comes off the row instead of ordering it from the schema, and
    /// it is the single most likely way for this to go wrong. So it is checked
    /// across the whole corpus rather than over three documents.
    /// </para>
    /// <para>
    /// Not every document is affected: <c>jsonb</c> orders members by key
    /// length and then by bytes, and a four-member glossary entry — key, name,
    /// contentSet, description — happens to already be in that order. There are
    /// a hundred or so of those, which is why the bound is "almost all" rather
    /// than "all".
    /// </para>
    /// </remarks>
    [Fact]
    public void OrderingMembersTheWayTheDatabaseReturnsThemWouldChangeAlmostEveryDocument()
    {
        var corpus = ContentCorpusRoundTripTests.Tree(ContentFixture.CommittedCorpus);

        corpus.Count.ShouldBeGreaterThan(7000, "the submodule corpus should be checked out in full");

        var affected = 0;
        var reproduced = new List<string>();

        foreach (var (relative, committed) in corpus)
        {
            var contentType = relative[..relative.IndexOf('/', StringComparison.Ordinal)];

            using var parsed = JsonDocument.Parse(committed);

            Canonical.Render(contentType, parsed.RootElement).ShouldBe(committed, relative);

            if (string.Equals(
                    Mutate("jsonb member order", contentType, parsed.RootElement),
                    committed,
                    StringComparison.Ordinal))
            {
                reproduced.Add(relative);
            }
            else
            {
                affected++;
            }
        }

        affected.ShouldBeGreaterThan(
            (int)(corpus.Count * 0.95),
            $"only {affected} of {corpus.Count} documents would be changed by writing members in " +
            "the database's order, which is too few for the round trip to be pinning member " +
            "order at all");

        // The ones that legitimately coincide are the short glossary entries.
        // If that set grows into something else, the bound above stops meaning
        // what it says.
        reproduced.ShouldAllBe(
            relative => CoincidentalTypes.Any(
                type => relative.StartsWith(type + "/", StringComparison.Ordinal)),
            "these documents are unaffected by member order and are not one of the short types " +
            "that explains it: " + string.Join(", ", reproduced.Take(10)));
    }

    private static string Mutate(string mutation, string contentType, JsonElement document)
    {
        if (string.Equals(mutation, "jsonb member order", StringComparison.Ordinal))
        {
            // Note what this does not do: hand the reordered document back to
            // the canonical writer. That would prove only that the writer is
            // order-independent, which it is. The mistake being simulated is an
            // exporter that writes the document out in the order it came off
            // the row, so the reordering and the writing have to be the same
            // step.
            return AsRead(Reordered(document));
        }

        var canonical = Canonical.Render(contentType, document);

        return mutation switch
        {
            "four-space indentation" => canonical.Replace("\n  ", "\n    ", StringComparison.Ordinal),

            "no indentation" =>
                string.Join('\n', canonical.Split('\n').Select(line => line.TrimStart(' '))),

            "CRLF line endings" => canonical.Replace("\n", "\r\n", StringComparison.Ordinal),

            "no trailing newline" => canonical.TrimEnd('\n'),

            _ => Escaped(canonical),
        };
    }

    /// <summary>
    /// The document written out in its own member order, and otherwise in the
    /// canonical format.
    /// </summary>
    /// <remarks>
    /// Same indentation, same newline, same encoder, same trailing newline —
    /// everything the canonical writer does except consult the schema. What is
    /// left is exactly the one difference under test.
    /// </remarks>
    private static string AsRead(JsonElement document)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       IndentSize = 2,
                       NewLine = "\n",
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The document with its members in the order PostgreSQL returns them.</summary>
    /// <remarks>
    /// <c>jsonb</c> stores an object's members sorted by key length and then by
    /// the key's bytes, and hands them back that way. Applied at every level,
    /// because that is where the exporter would be reading them from.
    /// </remarks>
    private static JsonElement Reordered(JsonElement document)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(document, writer);
        }

        using var parsed = JsonDocument.Parse(buffer.ToArray());

        // Cloned so it outlives the document it was parsed from.
        return parsed.RootElement.Clone();

        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (var member in value.EnumerateObject()
                                 .OrderBy(member => member.Name.Length)
                                 .ThenBy(member => member.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(member.Name);
                        Write(member.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    foreach (var item in value.EnumerateArray())
                    {
                        Write(item, writer);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }

    /// <summary>What the default encoder would have done to the non-ASCII characters.</summary>
    private static string Escaped(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (character > 127)
            {
                builder.Append("\\u").Append(((int)character).ToString("x4"));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
