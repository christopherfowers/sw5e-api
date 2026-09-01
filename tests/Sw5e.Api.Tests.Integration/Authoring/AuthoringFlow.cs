using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// Helpers for the authoring tests: documents to publish, and direct reads of
/// the store so a refusal can be checked against what was actually written
/// rather than only against a status code.
/// </summary>
/// <remarks>
/// Every refusal in this suite is asserted twice — once on the response and
/// once on the database — because a handler that returns 400 and writes anyway
/// passes the first assertion. The second is the one that means anything.
/// </remarks>
internal static class AuthoringFlow
{
    /// <summary>
    /// The content type these tests author. Its schema is four fields, all
    /// required, with <c>additionalProperties: false</c> — so "valid" and
    /// "invalid" are both easy to state exactly, and both are stated by the
    /// real published schema rather than by the test.
    /// </summary>
    public const string Type = "armor-property";

    /// <summary>A document that conforms.</summary>
    public static JsonElement Valid(string key, string name, string description) =>
        Parse($$"""
            {
              "key": {{JsonSerializer.Serialize(key)}},
              "name": {{JsonSerializer.Serialize(name)}},
              "contentSet": "core",
              "description": {{JsonSerializer.Serialize(description)}}
            }
            """);

    /// <summary>
    /// A document that does not conform: <c>description</c> is required and
    /// absent, and <c>quantumEntanglement</c> is not a property the schema
    /// allows.
    /// </summary>
    public static JsonElement SchemaViolating(string key) =>
        Parse($$"""
            {
              "key": {{JsonSerializer.Serialize(key)}},
              "name": "Malformed",
              "contentSet": "core",
              "quantumEntanglement": true
            }
            """);

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static Task<HttpResponseMessage> SaveDraftAsync(
        HttpClient client,
        string key,
        JsonElement document,
        Guid? resolvesFlagId = null) =>
        client.PutAsJsonAsync(
            $"/api/authoring/drafts/{Type}/{key}",
            new { document, resolvesFlagId });

    public static Task<HttpResponseMessage> PublishAsync(
        HttpClient client,
        string key,
        string? reason = null) =>
        client.PostAsJsonAsync($"/api/authoring/drafts/{Type}/{key}/publish", new { reason });

    public static Task<HttpResponseMessage> RevertAsync(
        HttpClient client,
        string key,
        long revisionId,
        string? reason = null) =>
        client.PostAsJsonAsync(
            $"/api/authoring/content/{Type}/{key}/revert",
            new { revisionId, reason });

    /// <summary>The catalogue row, read straight out of the database.</summary>
    public static async Task<ContentItemRow?> StoredItemAsync(
        AuthoringApiFactory factory,
        string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        return await database.ContentItems
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ContentType == Type && item.ItemKey == key);
    }

    /// <summary>The draft row, read straight out of the database.</summary>
    public static async Task<ContentDraftRow?> StoredDraftAsync(
        AuthoringApiFactory factory,
        string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        return await database.ContentDrafts
            .AsNoTracking()
            .SingleOrDefaultAsync(draft => draft.ContentType == Type && draft.ItemKey == key);
    }

    /// <summary>Every revision of one document, oldest first.</summary>
    public static async Task<List<ContentRevisionRow>> StoredRevisionsAsync(
        AuthoringApiFactory factory,
        string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        return await database.ContentRevisions
            .AsNoTracking()
            .Where(revision => revision.ContentType == Type && revision.ItemKey == key)
            .OrderBy(revision => revision.Number)
            .ToListAsync();
    }

    /// <summary>A key nothing else in the suite will use.</summary>
    /// <remarks>
    /// Trimmed to the shorter of the string and the cut. A range that runs past
    /// the end throws rather than stopping there, so a short label — which is
    /// most of them, since a name plus a 32-character identifier is only 37
    /// characters — would fail before the request under test was ever made.
    /// </remarks>
    public static string NewKey(string label)
    {
        var candidate = $"{label}-{Guid.NewGuid():N}";

        return candidate[..Math.Min(40, candidate.Length)].TrimEnd('-');
    }
}
