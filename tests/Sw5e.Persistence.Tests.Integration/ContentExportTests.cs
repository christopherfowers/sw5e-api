using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// What the exporter writes, and what it refuses to.
/// </summary>
/// <remarks>
/// The round trip over the whole corpus is in
/// <see cref="ContentCorpusRoundTripTests"/>. This is about the decisions
/// around it: which rows count as published, what happens to a file the
/// catalogue no longer has, what a subset export is allowed to touch, and what
/// an operator sees when the two sides disagree. All of it over the small
/// fixture, because none of it is about volume.
/// </remarks>
public sealed class ContentExportTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "content_export";

    private static readonly Guid Author = Guid.Parse("7a1b0f1e-0c4f-4f2f-9f0a-4d3c2b1a0e9d");

    [DockerFact]
    public async Task ADraftIsNotPublishedContentAndIsNotExported()
    {
        using var destination = TemporaryDirectory.Create();

        await ExportAsync(new ContentExportRequest(destination.Path));

        var before = Read(destination.Path, "species", "wookiee");

        var draft = await WithHomeworld("wookiee", "Somewhere Else Entirely");

        var saved = await AuthoringAsync(store => store.SaveDraftAsync(
            Type("species"), "wookiee", draft.RootElement, Author, null));

        saved.Status.ShouldBe(ContentAuthoringStatus.Succeeded);

        var result = await ExportAsync(new ContentExportRequest(destination.Path));

        result.InAgreement.ShouldBeTrue(
            "an unpublished draft changed the exported tree: " +
            string.Join(", ", result.Changes));

        Read(destination.Path, "species", "wookiee").ShouldBe(before);
        before.ShouldNotContain("Somewhere Else Entirely");
    }

    [DockerFact]
    public async Task PublishingADraftChangesTheExportedDocumentAndRevertingPutsItBackExactly()
    {
        using var destination = TemporaryDirectory.Create();

        await ExportAsync(new ContentExportRequest(destination.Path));

        var original = Read(destination.Path, "species", "wookiee");

        var draft = await WithHomeworld("wookiee", "Somewhere Else Entirely");

        await AuthoringAsync(store => store.SaveDraftAsync(
            Type("species"), "wookiee", draft.RootElement, Author, null));

        var published = await AuthoringAsync(store => store.PublishDraftAsync(
            Type("species"), "wookiee", Author, "testing the exporter"));

        published.Status.ShouldBe(ContentAuthoringStatus.Succeeded);

        var afterPublish = await ExportAsync(new ContentExportRequest(destination.Path));

        afterPublish.Changes.ShouldHaveSingleItem().ShouldBe(
            new ContentExportChange("species", "wookiee", ContentExportOutcome.Changed));

        Read(destination.Path, "species", "wookiee")
            .ShouldContain("Somewhere Else Entirely");

        // The revision the publish wrote over: the document as it was imported,
        // recorded lazily the first time somebody edited it.
        var history = await AuthoringAsync(store =>
            store.ListRevisionsAsync(Type("species"), "wookiee", 10));
        var baseline = history.Last();

        var reverted = await AuthoringAsync(store => store.RevertAsync(
            Type("species"), "wookiee", baseline.Id, Author, "and back again"));

        reverted.Status.ShouldBe(ContentAuthoringStatus.Succeeded);

        var afterRevert = await ExportAsync(new ContentExportRequest(destination.Path));

        afterRevert.Changes.ShouldHaveSingleItem().ShouldBe(
            new ContentExportChange("species", "wookiee", ContentExportOutcome.Changed));

        // Byte for byte, not merely equivalent. A revert that came back with
        // the same document formatted differently would produce a diff saying
        // the file changed when nothing about it did.
        Read(destination.Path, "species", "wookiee").ShouldBe(original);
    }

    [DockerFact]
    public async Task AWithdrawnDocumentIsRemovedFromTheTree()
    {
        using var destination = TemporaryDirectory.Create();

        await ExportAsync(new ContentExportRequest(destination.Path));

        File.Exists(Path.Combine(destination.Path, "feat", "sharpshooter.json")).ShouldBeTrue();

        await using (var context = Database.CreateContext())
        {
            await context.ContentItems
                .Where(item => item.ContentType == "feat" && item.ItemKey == "sharpshooter")
                .ExecuteDeleteAsync();
        }

        var result = await ExportAsync(new ContentExportRequest(destination.Path));

        result.Changes.ShouldHaveSingleItem().ShouldBe(
            new ContentExportChange("feat", "sharpshooter", ContentExportOutcome.Removed));

        File.Exists(Path.Combine(destination.Path, "feat", "sharpshooter.json")).ShouldBeFalse();
    }

    /// <summary>
    /// An empty type is treated as a failed read, not as a deletion.
    /// </summary>
    /// <remarks>
    /// The importer refuses to empty a type it found no files for, for the same
    /// reason and in the other direction: a half-applied migration, a filtered
    /// query or a database that was never populated all look exactly like
    /// somebody deliberately withdrawing every monster, and only one of those
    /// is likely.
    /// </remarks>
    [DockerFact]
    public async Task AnEmptyTypeIsNotEmptiedFromTheTree()
    {
        using var destination = TemporaryDirectory.Create();

        await ExportAsync(new ContentExportRequest(destination.Path));

        var before = Directory.GetFiles(Path.Combine(destination.Path, "monster")).Length;

        before.ShouldBeGreaterThan(0);

        await using (var context = Database.CreateContext())
        {
            await context.ContentItems.Where(item => item.ContentType == "monster").ExecuteDeleteAsync();
        }

        var result = await ExportAsync(new ContentExportRequest(destination.Path));

        result.Changes.ShouldBeEmpty();
        result.Warnings.ShouldContain(warning => warning.Contains("'monster'", StringComparison.Ordinal));

        Directory.GetFiles(Path.Combine(destination.Path, "monster")).Length.ShouldBe(before);
    }

    [DockerFact]
    public async Task CheckReportsTheDisagreementAndWritesNothing()
    {
        using var destination = TemporaryDirectory.Create();

        await ExportAsync(new ContentExportRequest(destination.Path));

        var original = Read(destination.Path, "species", "wookiee");

        var draft = await WithHomeworld("wookiee", "Somewhere Else Entirely");

        await AuthoringAsync(store => store.SaveDraftAsync(
            Type("species"), "wookiee", draft.RootElement, Author, null));

        await AuthoringAsync(store => store.PublishDraftAsync(
            Type("species"), "wookiee", Author, null));

        var result = await ExportAsync(
            new ContentExportRequest(destination.Path, CheckOnly: true));

        result.InAgreement.ShouldBeFalse();
        result.Changed.ShouldBe(1);
        result.Changes.ShouldHaveSingleItem().ToString().ShouldBe("species/wookiee.json: differs");

        Read(destination.Path, "species", "wookiee").ShouldBe(original);
    }

    [DockerTheory]
    [InlineData("species", null, "species")]
    [InlineData("species", "wookiee", "species")]
    public async Task ASubsetExportTouchesOnlyWhatItWasAskedFor(
        string type,
        string? key,
        string expectedDirectory)
    {
        using var destination = TemporaryDirectory.Create();

        var result = await ExportAsync(new ContentExportRequest(destination.Path, type, key));

        Directory.GetDirectories(destination.Path)
                 .Select(Path.GetFileName)
                 .ShouldBe([expectedDirectory]);

        result.Examined.ShouldBe(
            key is null ? ContentFixture.ExpectedCounts[type] : 1);
    }

    [DockerFact]
    public async Task ASingleDocumentExportRefusesToPruneAnythingElse()
    {
        using var destination = TemporaryDirectory.Create();

        await ExportAsync(new ContentExportRequest(destination.Path));

        await using (var context = Database.CreateContext())
        {
            await context.ContentItems
                .Where(item => item.ContentType == "species" && item.ItemKey != "wookiee")
                .ExecuteDeleteAsync();
        }

        // Prune is asked for and is still not applied: the run was narrowed to
        // one document, and one document says nothing about the others.
        var result = await ExportAsync(
            new ContentExportRequest(destination.Path, "species", "wookiee", Prune: true));

        result.Changes.ShouldBeEmpty();

        Directory.GetFiles(Path.Combine(destination.Path, "species")).Length
                 .ShouldBe(ContentFixture.ExpectedCounts["species"]);
    }

    [DockerTheory]
    [InlineData(null, "wookiee")]
    [InlineData("not-a-content-type", null)]
    public async Task AnIncoherentRequestIsRefused(string? type, string? key)
    {
        using var destination = TemporaryDirectory.Create();

        await Should.ThrowAsync<ArgumentException>(
            () => ExportAsync(new ContentExportRequest(destination.Path, type, key)));
    }

    /// <summary>
    /// A document the schema rejects stops the run, and nothing is written.
    /// </summary>
    /// <remarks>
    /// Reachable without going near the authoring endpoints, which validate:
    /// the importer does not, and neither does a migration or a hand-written
    /// UPDATE. The content repository's CI validates every document on every
    /// pull request, so an export that emitted one of these would produce a
    /// branch that cannot be merged — discovered by whoever opened the pull
    /// request rather than by whoever ran the export.
    /// </remarks>
    [DockerFact]
    public async Task ADocumentThatFailsItsSchemaStopsTheExport()
    {
        using var destination = TemporaryDirectory.Create();

        await using (var context = Database.CreateContext())
        {
            var row = await context.ContentItems.SingleAsync(
                item => item.ContentType == "feat" && item.ItemKey == "sharpshooter");

            // Valid JSON, valid as a row, and not a feat: the schema requires a
            // description and forbids anything it does not declare.
            row.Body = """{"key":"sharpshooter","name":"Sharpshooter","invented":true}""";

            await context.SaveChangesAsync();
        }

        var failure = await Should.ThrowAsync<InvalidOperationException>(
            () => ExportAsync(new ContentExportRequest(destination.Path)));

        failure.Message.ShouldContain("feat/sharpshooter.json");

        Directory.Exists(destination.Path).ShouldBeTrue();
        Directory.GetFileSystemEntries(destination.Path).ShouldBeEmpty(
            "the export wrote part of a tree before refusing, which is the outcome the " +
            "two-pass write exists to avoid");
    }

    /// <summary>Runs one authoring operation in a scope of its own.</summary>
    private async Task<T> AuthoringAsync<T>(Func<IContentAuthoringStore, Task<T>> operation)
    {
        using var scope = Database.Services.CreateScope();

        return await operation(scope.ServiceProvider.GetRequiredService<IContentAuthoringStore>());
    }

    private async Task<ContentExportResult> ExportAsync(ContentExportRequest request)
    {
        using var scope = Database.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<ContentExporter>()
            .ExportAsync(request);
    }

    private static ContentTypeDefinition Type(string key) =>
        ContentTypeRegistry.TryResolve(key, out var definition)
            ? definition
            : throw new ArgumentException($"'{key}' is not a content type.", nameof(key));

    /// <summary>The published document with one field changed.</summary>
    private async Task<JsonDocument> WithHomeworld(string key, string homeworld)
    {
        await using var context = Database.CreateContext();

        var row = await context.ContentItems.SingleAsync(
            item => item.ContentType == "species" && item.ItemKey == key);

        using var current = JsonDocument.Parse(row.Body);
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var member in current.RootElement.EnumerateObject())
            {
                if (string.Equals(member.Name, "homeworld", StringComparison.Ordinal))
                {
                    writer.WriteString("homeworld", homeworld);
                    continue;
                }

                member.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    private static string Read(string root, string type, string key) =>
        File.ReadAllText(Path.Combine(root, type, key + ".json"), Encoding.UTF8)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
