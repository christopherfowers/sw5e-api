using Microsoft.EntityFrameworkCore;

using Shouldly;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Every cross-reference the committed corpus declares reaches something —
/// except for a named list of things nobody has written yet.
/// </summary>
/// <remarks>
/// <para>
/// A dangling reference is the quietest kind of content bug there is. Nothing
/// throws, nothing 500s, no import fails: the edge is stored with a null
/// target and the item's page prints the clause exactly as it was written. So
/// the guard shoto spent an unknown length of time telling readers it had a
/// property called "light luminous" — a missing comma between two real
/// properties — and every deploy was green the whole time.
/// </para>
/// <para>
/// The importer already counts these, and has been reporting six of them on
/// every QA deploy for as long as anybody has looked at the summary rather than
/// the exit status. Counting without failing is how a number sits in a log for
/// months. This is the assertion that was missing, not the detection.
/// </para>
/// <para>
/// It asserts the exact set rather than a count, because a count going from six
/// to six tells nobody that one reference was fixed and a different one broke.
/// Fixing something here means deleting a line, and the failure message names
/// what to delete.
/// </para>
/// <para>
/// Written here rather than in the content repository on purpose. Resolving a
/// property clause means parsing it — stripping the parenthetical, then the
/// numeric argument — and that parser lives in <c>ContentReferenceMap</c>. A
/// copy of it next to the corpus would agree with this one exactly until the
/// day somebody changed one of them, and the whole value of the check is that
/// it agrees with what the importer actually does.
/// </para>
/// </remarks>
public sealed class ContentCorpusReferenceTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "corpus_references";

    /// <summary>The corpus is imported by the test, not the fixture.</summary>
    protected override bool ImportContent => false;

    /// <summary>
    /// What the corpus is allowed to fail to resolve, and why.
    /// </summary>
    /// <remarks>
    /// One missing document, cited by four weapons. The retrosaber, vibroflail,
    /// warsaber and warsword all carry "reckless 1", in exactly the grammar
    /// that "vicious 1" and "dire 1" use, and there is no reckless property in
    /// the glossary. The clause parses correctly; the thing it names does not
    /// exist.
    ///
    /// That is a gap in the corpus rather than a defect in this code, and the
    /// rules text is the content owner's to write — inventing it here would put
    /// a game rule nobody agreed to in front of readers. So it is listed,
    /// which is the difference between a known gap and an unnoticed one.
    /// </remarks>
    private static readonly (string TargetType, string Identifier, int Count)[] KnownGaps =
    [
        ("weapon-property", "reckless", 4),
    ];

    [DockerFact]
    public async Task EveryReferenceInTheCorpusReachesSomethingItWasMeantTo()
    {
        Directory.Exists(ContentFixture.CommittedCorpus).ShouldBeTrue(
            $"No corpus at '{ContentFixture.CommittedCorpus}'. Initialise the submodule with " +
            "'git submodule update --init'.");

        var imported = await Database.ImportAsync(ContentFixture.CommittedCorpus);

        // A partial import would leave this passing over whatever fraction
        // happened to load, and a corpus that mostly did not import has very
        // few references to dangle.
        imported.Inserted.ShouldBeGreaterThan(
            7000, "the whole corpus should have imported, not part of it");

        await using var database = Database.CreateContext();

        var dangling = await database.ContentReferences
            .Where(reference => reference.ResolvedItemId == null)
            .GroupBy(reference => new { reference.TargetType, reference.TargetIdentifier })
            .Select(group => new
            {
                group.Key.TargetType,
                group.Key.TargetIdentifier,
                Count = group.Count(),
            })
            .ToListAsync();

        var found = dangling
            .Select(row => (row.TargetType, Identifier: row.TargetIdentifier, row.Count))
            .OrderBy(row => row.TargetType, StringComparer.Ordinal)
            .ThenBy(row => row.Identifier, StringComparer.Ordinal)
            .ToArray();

        var expected = KnownGaps
            .OrderBy(row => row.TargetType, StringComparer.Ordinal)
            .ThenBy(row => row.Identifier, StringComparer.Ordinal)
            .ToArray();

        /*
          Named in the message, because the useful half of this failing is
          knowing which document to open. "Expected 1 but found 3" sends
          somebody back to the database to ask what the other two were.
        */
        found.ShouldBe(
            expected,
            "the corpus declares references that reach nothing:" +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                found.Select(row =>
                    $"  {row.TargetType} '{row.Identifier}' — cited {row.Count} time" +
                    (row.Count == 1 ? string.Empty : "s"))) +
            Environment.NewLine +
            "Either the target is missing from the corpus, or the citing document " +
            "has it misspelled. If a gap is deliberate, add it to KnownGaps with " +
            "the reason.");
    }
}
