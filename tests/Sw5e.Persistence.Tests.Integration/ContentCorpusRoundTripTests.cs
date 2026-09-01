using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Import the content repository's whole corpus, export it again, and get back
/// the files that are committed there — byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// This is the property the exporter exists for, and it is not a property of
/// the exporter alone. PostgreSQL stores each document as <c>jsonb</c>, which
/// keeps the values and discards the text: member order, indentation and
/// whitespace are all gone by the time a row is read back. So the file that
/// comes out is derived, not remembered, and if the derivation disagrees with
/// the committed file in any way at all, every export produces a pull request
/// full of reformatting with the actual change buried somewhere inside it.
/// Nobody reviews that pull request properly twice.
/// </para>
/// <para>
/// It runs against the real corpus at the pinned submodule commit rather than
/// against the small fixture the rest of this project uses. The fixture holds
/// 321 documents chosen for the shapes they exercise; the corpus holds 7,877
/// including every awkward one — the em dash, the replacement character the
/// scrape left behind, <c>0.0</c> and <c>0.3333333333333333</c>, an em space in
/// a starship rule. Those are exactly the documents a formatting difference
/// hides in.
/// </para>
/// <para>
/// The two assertions are in one test because each needs the corpus in the
/// database and importing it is the expensive part. xUnit builds a class per
/// test method, so splitting them would import 7,877 documents twice to prove
/// two halves of one thing.
/// </para>
/// </remarks>
public sealed class ContentCorpusRoundTripTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "corpus_round_trip";

    /// <summary>
    /// The corpus is imported by the test; the base class would import the
    /// small fixture.
    /// </summary>
    protected override bool ImportContent => false;

    [DockerFact]
    public async Task ExportingTheImportedCorpusReproducesEveryCommittedFile()
    {
        Directory.Exists(ContentFixture.CommittedCorpus).ShouldBeTrue(
            $"No corpus at '{ContentFixture.CommittedCorpus}'. Initialise the submodule with " +
            "'git submodule update --init'.");

        var imported = await Database.ImportAsync(ContentFixture.CommittedCorpus);

        // A partial import would make everything below pass over whatever
        // fraction did load.
        imported.Inserted.ShouldBeGreaterThan(
            7000, "the whole corpus should have imported, not part of it");

        using var destination = TemporaryDirectory.Create();

        var result = await ExportAsync(new ContentExportRequest(destination.Path));

        result.Examined.ShouldBe(imported.Inserted + imported.Updated + imported.Unchanged);

        var committed = Tree(ContentFixture.CommittedCorpus);
        var exported = Tree(destination.Path);

        committed.Count.ShouldBe(result.Examined);

        // Named before compared, so a dropped document reads as "the export
        // lost it" rather than as a difference in file 4,912 of 7,877.
        exported.Keys.Order(StringComparer.Ordinal).ShouldBe(
            committed.Keys.Order(StringComparer.Ordinal));

        var differing = committed.Keys
            .Where(path => !string.Equals(committed[path], exported[path], StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        differing.ShouldBeEmpty(
            $"{differing.Count} of {committed.Count} exported documents differ from what the " +
            "content repository has committed:" + Environment.NewLine +
            string.Join(
                Environment.NewLine,
                differing.Take(10).Select(path => $"  {path}: {Difference(committed[path], exported[path])}")));

        // Importing what was just exported has to change nothing. Byte
        // equality above already implies it, but only through the change token
        // — which is a hash of the file as it sits on disk — so stating it
        // separately is what would catch a version scheme that stopped
        // depending on the bytes. It is also the property a deploy relies on: a
        // pull request built from an export must not churn every row when it
        // lands.
        var reimported = await Database.ImportAsync(destination.Path);

        reimported.Inserted.ShouldBe(0);
        reimported.Updated.ShouldBe(0);
        reimported.Deleted.ShouldBe(0);
        reimported.Unchanged.ShouldBe(committed.Count);

        // And the operator-facing half of the same property, against the
        // checkout itself rather than a temporary directory: an export that
        // reported changes every time it ran would be useless for answering
        // "has anything been published since last time?", which is the question
        // --check exists for.
        var check = await ExportAsync(
            new ContentExportRequest(ContentFixture.CommittedCorpus, CheckOnly: true));

        check.InAgreement.ShouldBeTrue(
            $"the catalogue and the committed tree disagree about {check.Changes.Count} " +
            "document(s):" + Environment.NewLine +
            string.Join(Environment.NewLine, check.Changes.Take(10).Select(change => $"  {change}")));

        check.Unchanged.ShouldBe(check.Examined);
    }

    private async Task<ContentExportResult> ExportAsync(ContentExportRequest request)
    {
        using var scope = Database.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ContentExporter>()
            .ExportAsync(request);
    }

    /// <summary>
    /// Every document under a content root, keyed by <c>type/key.json</c>.
    /// </summary>
    /// <remarks>
    /// Line endings are normalised and nothing else is. A working tree's line
    /// endings belong to git — the content repository pins them to LF, and a
    /// checkout made before it did holds CRLF — and this compares what the
    /// exporter derived against what was committed, not what git handed out.
    /// </remarks>
    internal static Dictionary<string, string> Tree(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var contentType = Path.GetFileName(directory);

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                files[$"{contentType}/{Path.GetFileName(file)}"] =
                    File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        return files;
    }

    /// <summary>Where two renderings first part company, in reviewable terms.</summary>
    private static string Difference(string committed, string exported)
    {
        var shared = 0;

        while (shared < committed.Length &&
               shared < exported.Length &&
               committed[shared] == exported[shared])
        {
            shared++;
        }

        var line = committed.Take(shared).Count(character => character == '\n') + 1;

        return $"line {line}: committed {Excerpt(committed, shared)} vs exported {Excerpt(exported, shared)}";

        static string Excerpt(string text, int from) =>
            from >= text.Length
                ? "(end of file)"
                : $"\"{text.Substring(from, Math.Min(48, text.Length - from)).Replace("\n", "\\n")}\"";
    }
}

/// <summary>A directory that removes itself.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path) => Path = path;

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sw5e-export-" + Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(path);

        return new TemporaryDirectory(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
