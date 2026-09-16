using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Shouldly;

using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Api.Tests.Integration.Moderation;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// What publishing tells an author about the consequences of publishing.
/// </summary>
/// <remarks>
/// <para>
/// The store already works all of this out. It extracts every reference a
/// document declares, resolves what it can against the catalogue, writes a null
/// target for the rest, and then answers 200 and says nothing. An author who
/// types "reckles" for a weapon property gets the same green tick as one who
/// spells it correctly, and the corpus carries a reference to nothing until
/// somebody reads an importer summary months later.
/// </para>
/// <para>
/// That is not hypothetical. It is how six unresolved references accumulated in
/// the corpus without anybody noticing, and the answer was being computed at
/// every single publish along the way.
/// </para>
/// <para>
/// These are notices rather than refusals, deliberately. Naming content that
/// does not exist yet is a normal way to author (the weapon before the
/// property, the creature before the power) and the store already re-resolves
/// waiting edges when the target arrives. Refusing would make the corpus
/// impossible to build in any order but one. What was missing was never a veto.
/// It was the sentence.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class PublishNoticeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// A book and two real weapon properties, published before anything else.
    /// </summary>
    /// <remarks>
    /// The fixture truncates the catalogue, so without this every document
    /// below would be unresolved in at least its <c>sourceKey</c> and the
    /// counts these tests assert would all be one higher for a reason that has
    /// nothing to do with what is being tested.
    /// </remarks>
    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();

        _client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(
            _factory, _client, "notice-author", Sw5eRoles.Administrator);

        await PublishAsync("source", "phb", AuthoringFlow.Parse("""
            {
              "key": "phb",
              "title": "Player's Handbook",
              "abbreviation": "PHB",
              "isOfficial": true
            }
            """));

        foreach (var property in new[] { "heavy", "two-handed" })
        {
            await PublishAsync("weapon-property", property, AuthoringFlow.Parse($$"""
                {
                  "key": {{JsonSerializer.Serialize(property)}},
                  "name": {{JsonSerializer.Serialize(property)}},
                  "contentSet": "core",
                  "description": "A property that exists."
                }
                """));
        }
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpResponseMessage> PublishAsync(
        string type,
        string key,
        JsonElement document)
    {
        (await _client.PutAsJsonAsync(
            $"/api/authoring/drafts/{type}/{key}",
            new { document, resolvesFlagId = (Guid?)null }))
            .IsSuccessStatusCode.ShouldBeTrue($"the {type} draft '{key}' should save");

        return await _client.PostAsJsonAsync(
            $"/api/authoring/drafts/{type}/{key}/publish",
            new { reason = (string?)null });
    }

    private Task<HttpResponseMessage> PublishWeaponAsync(string key, params string[] properties) =>
        PublishAsync("equipment", key, AuthoringFlow.Parse($$"""
            {
              "key": {{JsonSerializer.Serialize(key)}},
              "name": {{JsonSerializer.Serialize(key)}},
              "sourceKey": "phb",
              "contentSet": "core",
              "category": "weapon",
              "costInCredits": 100,
              "weight": 3,
              "stealthDisadvantage": false,
              "properties": {{JsonSerializer.Serialize(properties)}}
            }
            """));

    private static async Task<string[]> NoticesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("notices")
            .EnumerateArray()
            .Select(notice => notice.GetProperty("message").GetString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>
    /// Naming a property that does not exist is published, and said out loud.
    /// </summary>
    /// <remarks>
    /// "reckless" is the real example. Four weapons in the corpus carry it in
    /// exactly the grammar "vicious 1" uses, and no such property was ever
    /// written, so four documents point at nothing, and did so silently for as
    /// long as anybody has looked.
    /// </remarks>
    [Fact]
    public async Task PublishingSaysWhenADocumentNamesSomethingThatDoesNotExist()
    {
        var response = await PublishWeaponAsync(AuthoringFlow.NewKey("warsword"), "reckless 1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notices = body.GetProperty("notices");

        notices.GetArrayLength().ShouldBe(1);
        notices[0].GetProperty("code").GetString().ShouldBe("unresolved-reference");

        // The name is the actionable half: it is what the author has to correct
        // or go and write.
        notices[0].GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("reckless");
    }

    /// <summary>
    /// A whole document says nothing, so a notice means something.
    /// </summary>
    /// <remarks>
    /// The assertion that keeps the rest honest. A notices list that is always
    /// populated is a banner an author learns to dismiss without reading, which
    /// is worse than silence because it looks like diligence.
    /// </remarks>
    [Fact]
    public async Task PublishingAWholeDocumentSaysNothing()
    {
        var response = await PublishWeaponAsync(AuthoringFlow.NewKey("vibroblade"), "heavy");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NoticesAsync(response)).ShouldBeEmpty();
    }

    /// <summary>
    /// The notice does not stop the publish.
    /// </summary>
    /// <remarks>
    /// Asserted against the database rather than the status code, for the reason
    /// the rest of this suite gives: a handler that answers 200 and writes
    /// nothing passes an assertion on the response alone.
    /// </remarks>
    [Fact]
    public async Task ANoticeDoesNotPreventTheDocumentBeingWritten()
    {
        var key = AuthoringFlow.NewKey("retrosaber");

        await PublishWeaponAsync(key, "reckless 1");

        var stored = await AuthoringFlow.StoredItemOfTypeAsync(_factory, "equipment", key);

        stored.ShouldNotBeNull();
        stored!.ItemKey.ShouldBe(key);
    }

    /// <summary>
    /// Every unresolved reference is named, not only the first.
    /// </summary>
    /// <remarks>
    /// A document is usually wrong in one place, but the guard shoto was wrong
    /// in a way that produced two, and an author told about one of two mistakes
    /// will fix one and believe they are finished.
    /// </remarks>
    [Fact]
    public async Task EveryUnresolvedReferenceIsNamed()
    {
        var response = await PublishWeaponAsync(
            AuthoringFlow.NewKey("guard-shoto"), "light luminous", "reckless 1");

        var messages = await NoticesAsync(response);

        messages.Length.ShouldBe(2);
        messages.ShouldContain(message => message.Contains("light luminous"));
        messages.ShouldContain(message => message.Contains("reckless"));
    }

    /// <summary>
    /// A reference that resolves is not mentioned.
    /// </summary>
    /// <remarks>
    /// Pinned separately because the cheapest wrong implementation reports every
    /// reference a document declares rather than the ones that failed, and on a
    /// document with one bad property out of eight, the difference between those
    /// two is the entire point.
    /// </remarks>
    [Fact]
    public async Task ReferencesThatResolveAreNotMentioned()
    {
        var response = await PublishWeaponAsync(
            AuthoringFlow.NewKey("bustersaber"), "heavy", "two-handed", "reckless 1");

        var messages = await NoticesAsync(response);

        messages.Length.ShouldBe(1);
        messages[0].ShouldContain("reckless");
    }
}
