using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// Hosts the API over the content fixture committed alongside these tests.
/// </summary>
/// <remarks>
/// The fixture lives in this repository rather than in the sibling content
/// repository on purpose: these tests assert on exact counts, orderings and
/// snippets, and none of that can hold against a corpus another project is
/// still filling in.
/// </remarks>
public class ContentApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Absolute path to the committed fixture, copied beside the test assembly.</summary>
    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "TestContent");

    protected virtual string ContentRootPath => FixturePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Content:RootPath", ContentRootPath);
}

/// <summary>
/// Hosts the API over a content directory that does not exist, which is the
/// state of a fresh clone before the content repository is checked out beside
/// it. The API is expected to serve an empty catalogue rather than fail to
/// start.
/// </summary>
public sealed class EmptyContentApiFactory : ContentApiFactory
{
    protected override string ContentRootPath =>
        Path.Combine(AppContext.BaseDirectory, "TestContent-does-not-exist");
}

internal static class JsonResponse
{
    /// <summary>
    /// Reads a response body as JSON. Cloned so the element outlives the
    /// document it was parsed from.
    /// </summary>
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static IEnumerable<JsonElement> Array(this JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray();

    public static string Text(this JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? string.Empty;
}
