using System.Text.Json;
using Shouldly;
using Microsoft.Extensions.DependencyInjection;
using Sw5e.Domain.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// A publish is not undone by the next import.
/// </summary>
/// <remarks>
/// <para>
/// This is the test for a bug that reached production and took a week to
/// notice. The importer's job is to make the database match the repository,
/// which was the whole truth while it was the only writer of that table. The
/// content tools made it a second writer, and nothing reconciled them: a
/// correction published through the tools survived until the next deploy, and
/// then the import quietly put the repository's version back.
/// </para>
/// <para>
/// Nothing was destroyed, because the publish is still a revision. But the live
/// document reverted, readers saw the old text again, and the only sign was a
/// person asking why their change had stopped working.
/// </para>
/// <para>
/// The suites missed it because every one of them published or imported, never
/// both in that order. So that order is what these tests do.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PublishingSurvivesImportTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "publish_survives_import";

    protected override bool ImportContent => false;

    private const string Changed = "Somewhere Else Entirely";

    [DockerFact]
    public async Task AnImportDoesNotUndoAPublish()
    {
        await Database.ImportAsync();
        await PublishHomeworldAsync();

        var second = await Database.ImportAsync();

        // The repository has not changed this document, so the import has
        // nothing to say about it and must not touch it.
        second.Preserved.ShouldBe(1);
        second.Updated.ShouldBe(0);

        (await HomeworldAsync()).ShouldBe(Changed);
    }

    [DockerFact]
    public async Task TheReasonIsInTheWarnings()
    {
        await Database.ImportAsync();
        await PublishHomeworldAsync();

        var second = await Database.ImportAsync();

        // Silence here is what made the original bug invisible. An operator
        // reading the import's output should be told it declined to write.
        second.Warnings.ShouldContain(warning =>
            warning.Contains("species/wookiee", StringComparison.Ordinal) &&
            warning.Contains("published", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task RepeatedImportsLeaveThePublishAlone()
    {
        await Database.ImportAsync();
        await PublishHomeworldAsync();

        // Every deploy runs one. The protection has to hold on all of them, not
        // only the first: a rule that forgets what it did is a rule that loses
        // the work on the second deploy instead of the first.
        for (var deploy = 0; deploy < 3; deploy++)
        {
            (await Database.ImportAsync()).Preserved.ShouldBe(1);
        }

        (await HomeworldAsync()).ShouldBe(Changed);
    }

    [DockerFact]
    public async Task AnUntouchedDocumentIsStillCountedUnchanged()
    {
        await Database.ImportAsync();
        await PublishHomeworldAsync();

        var second = await Database.ImportAsync();

        // The guard on the guard. If everything were preserved, the first test
        // would pass while the importer had simply stopped working.
        second.Unchanged.ShouldBe(ContentFixture.ExpectedTotal - 1);
        second.Inserted.ShouldBe(0);
    }

    private static readonly Guid Author = Guid.Parse("7a1b0f1e-0c4f-4f2f-9f0a-4d3c2b1a0e9d");

    private async Task<T> AuthoringAsync<T>(Func<IContentAuthoringStore, Task<T>> operation)
    {
        using var scope = Database.Services.CreateScope();

        return await operation(scope.ServiceProvider.GetRequiredService<IContentAuthoringStore>());
    }

    private static ContentTypeDefinition Type(string key) =>
        ContentTypeRegistry.TryResolve(key, out var definition)
            ? definition
            : throw new ArgumentException($"'{key}' is not a content type.", nameof(key));

    private async Task PublishHomeworldAsync()
    {
        var current = await Database.Repository.GetAsync(Type("species"), "wookiee");
        current.ShouldNotBeNull();

        var document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            current!.Body.GetRawText())!;

        document["homeworld"] = JsonSerializer.SerializeToElement(Changed);

        using var edited = JsonDocument.Parse(JsonSerializer.Serialize(document));

        var saved = await AuthoringAsync(store => store.SaveDraftAsync(
            Type("species"), "wookiee", edited.RootElement, Author, null));
        saved.Status.ShouldBe(ContentAuthoringStatus.Succeeded);

        var published = await AuthoringAsync(store => store.PublishDraftAsync(
            Type("species"), "wookiee", Author, "a correction somebody made"));
        published.Status.ShouldBe(ContentAuthoringStatus.Succeeded);

        (await HomeworldAsync()).ShouldBe(Changed);
    }

    private async Task<string?> HomeworldAsync()
    {
        var document = await Database.Repository.GetAsync(Type("species"), "wookiee");

        return document?.Body.TryGetProperty("homeworld", out var value) == true
            ? value.GetString()
            : null;
    }
}
