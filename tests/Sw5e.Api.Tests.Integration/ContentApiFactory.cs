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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Content:RootPath", ContentRootPath);

        // The identity registration insists on a connection string, because a
        // deployment without one has no accounts and should say so at startup
        // rather than at the first sign-in. These tests are about content and
        // never touch an account, so a well-formed placeholder is enough: EF
        // Core parses it while composing the context and opens nothing until
        // somebody queries. If a content test ever does make the API talk to
        // the identity store, it will fail here with a connection error, which
        // is the correct and informative outcome.
        builder.UseSetting(
            "ConnectionStrings:Sw5eIdentity",
            "Host=127.0.0.1;Port=1;Database=sw5e_identity_unused;Username=unused");

        // Data protection eagerly loads its key ring during startup so that a
        // broken key store is reported at boot rather than at the first
        // request. Pointed at the placeholder above it fails every time and
        // logs the whole connection stack, which would bury the output of every
        // content test in an error that is expected and irrelevant here. The
        // behaviour is deliberately not changed — only its volume in this one
        // fixture.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.DataProtection", "None");
    }
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
