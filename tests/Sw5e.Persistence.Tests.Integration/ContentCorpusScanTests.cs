using Shouldly;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// Every document in the committed corpus survives the scan.
/// </summary>
/// <remarks>
/// <para>
/// The scanner does not throw on a document it cannot file. It records a
/// warning and drops it, which is the right behaviour for one corrupt file
/// among eight thousand and the wrong shape of news when it happens to every
/// document of a type: the type appears to work, the API serves an empty list,
/// and nothing anybody looks at says why.
/// </para>
/// <para>
/// That is exactly what happened when this type registry grew. The projection
/// table defaults the display-name field to <c>name</c>, channels are named by
/// their service and pages by the route they belong to, so every document of
/// both was rejected for missing a property neither was ever meant to carry.
/// Four documents went quietly missing and the only thing that noticed was the
/// corpus round trip, which needs a database and a container and three minutes.
/// </para>
/// <para>
/// This needs none of those. It reads the corpus off disk, so it runs on a
/// machine with no Docker daemon and fails in seconds, next to the change that
/// caused it.
/// </para>
/// </remarks>
public sealed class ContentCorpusScanTests
{
    [Fact]
    public void ScanningTheCommittedCorpusDropsNothing()
    {
        Directory.Exists(ContentFixture.CommittedCorpus).ShouldBeTrue(
            $"No corpus at '{ContentFixture.CommittedCorpus}'. Initialise the submodule with " +
            "'git submodule update --init'.");

        var result = ContentIndexBuilder.Build(ContentFixture.CommittedCorpus);

        result.Warnings.ShouldBeEmpty(
            "every document in the corpus should have been filed. A warning here is a " +
            "document the API will not serve:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Warnings.Take(10).Select(each => $"  {each}")));

        // And the count matches the files on disk, which catches a whole type
        // being skipped before it is read rather than after.
        var onDisk = ContentTypeRegistry.All
            .Select(definition => Path.Combine(ContentFixture.CommittedCorpus, definition.Key))
            .Where(Directory.Exists)
            .Sum(directory => Directory.EnumerateFiles(directory, "*.json").Count());

        result.ItemCount.ShouldBe(onDisk);
    }

    /// <summary>
    /// Every registered type has at least one document in the corpus.
    /// </summary>
    /// <remarks>
    /// Not a rule about content. It is a rule about this test: the assertion
    /// above can only prove a type projects if there is a document of that type
    /// to project, so a registered type with an empty directory would pass it
    /// while proving nothing. Registering a type without adding a document is a
    /// legitimate thing to want, and when it happens this is the line to change,
    /// deliberately, rather than the guard above to weaken.
    /// </remarks>
    [Fact]
    public void EveryRegisteredTypeHasSomethingToScan()
    {
        var empty = ContentTypeRegistry.All
            .Where(definition =>
            {
                var directory = Path.Combine(ContentFixture.CommittedCorpus, definition.Key);
                return !Directory.Exists(directory) ||
                       !Directory.EnumerateFiles(directory, "*.json").Any();
            })
            .Select(definition => definition.Key)
            .ToList();

        empty.ShouldBeEmpty(
            "these types are registered and the corpus has no document of them, so nothing " +
            "proves they project: " + string.Join(", ", empty));
    }
}
