using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Sw5e.Api.Tests.Integration;

/// <summary>
/// The frontend generates its HTTP client from this document, so a wrong or
/// missing entry here is a compile error in another repository rather than
/// something anyone notices at runtime.
/// </summary>
public sealed class OpenApiDocumentTests(ContentApiFactory factory)
    : IClassFixture<ContentApiFactory>
{
    private static async Task<JsonElement> DocumentAsync(ContentApiFactory factory)
    {
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await JsonResponse.ReadAsync(response);
    }

    [Theory]
    [InlineData("/api/content-types")]
    [InlineData("/api/content/{type}")]
    [InlineData("/api/content/{type}/{key}")]
    [InlineData("/api/search")]
    [InlineData("/api/auth/register")]
    [InlineData("/api/auth/email/verify")]
    [InlineData("/api/auth/email/code")]
    [InlineData("/api/auth/email/code/verify")]
    [InlineData("/api/auth/passkey/register/begin")]
    [InlineData("/api/auth/passkey/register/complete")]
    [InlineData("/api/auth/passkey/{credentialId}")]
    [InlineData("/api/auth/passkey/login/begin")]
    [InlineData("/api/auth/passkey/login/complete")]
    [InlineData("/api/auth/mfa/totp/enroll")]
    [InlineData("/api/auth/mfa/totp/verify")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/flags")]
    [InlineData("/api/flags/mine")]
    [InlineData("/api/flags/summary")]
    [InlineData("/api/flags/{flagId}/status")]
    [InlineData("/api/site/environment")]
    public async Task Document_DescribesEveryContentRoute(string path)
    {
        var document = await DocumentAsync(factory);

        document.GetProperty("paths").TryGetProperty(path, out _).ShouldBeTrue(
            $"the generated client needs an entry for {path}");
    }

    /// <summary>
    /// Operation ids become method names in the generated client, so they have
    /// to be present, stable and distinct. Left unset, the generator falls back
    /// to something derived from the route and changes whenever the route does.
    /// </summary>
    [Fact]
    public async Task Document_GivesEveryContentOperationAnId()
    {
        var document = await DocumentAsync(factory);

        var ids = document.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject())
            .Select(operation => operation.Value.GetProperty("operationId").GetString())
            .ToArray();

        // Exhaustive rather than a subset check. An operation added without an
        // id would otherwise slip through, and the generated client would name
        // its method after the route — which then changes whenever the route
        // does.
        ids.ShouldBe(
            [
                "listContentTypes", "listContent", "getContentItem", "searchContent",
                "HealthThroughProxy",
                "getSiteEnvironment",
                "register", "verifyEmail",
                "requestSignInCode", "verifySignInCode",
                "beginPasskeyRegistration", "completePasskeyRegistration", "removePasskey",
                "beginPasskeyLogin", "completePasskeyLogin",
                "enrollTotp", "verifyTotp",
                "logout", "currentUser", "assignRoles",
                "raiseFlag", "listOwnFlags",
                "listFlags", "summariseFlags", "updateFlagStatus",
                "getContentSchema",
                "listContentDrafts", "getContentDraft", "saveContentDraft",
                "discardContentDraft", "publishContentDraft",
                "listContentRevisions", "getContentRevision", "revertContent",
            ],
            ignoreOrder: true);
    }

    /// <summary>
    /// Every query parameter a caller may send has to appear, or the generated
    /// client simply cannot express filtering, sorting or paging.
    /// </summary>
    [Fact]
    public async Task Document_DescribesTheListParameters()
    {
        var document = await DocumentAsync(factory);

        var parameters = document.GetProperty("paths")
            .GetProperty("/api/content/{type}")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(parameter => parameter.Text("name"))
            .ToArray();

        parameters.ShouldBe(
            ["type", "page", "pageSize", "name", "source", "contentSet", "sort", "direction"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Document_DescribesTheErrorResponses()
    {
        var document = await DocumentAsync(factory);

        var responses = document.GetProperty("paths")
            .GetProperty("/api/content/{type}/{key}")
            .GetProperty("get")
            .GetProperty("responses");

        foreach (var status in new[] { "200", "304", "400", "404" })
        {
            responses.TryGetProperty(status, out _).ShouldBeTrue(
                $"the document must describe the {status} response");
        }
    }
}
