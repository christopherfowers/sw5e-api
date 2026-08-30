using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sw5e.Email.Providers.MailerSend;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the MailerSend adapter against a stub sitting at the transport
/// boundary.
/// </summary>
/// <remarks>
/// The assertions are about the bytes MailerSend would receive and about what
/// the adapter concludes from each answer it can get back. Nothing here checks
/// that a collaborator was called: the point of the stub is to let the real
/// request survive long enough to be inspected, not to stand in for the code
/// under test.
/// </remarks>
public sealed class MailerSendEmailSenderTests
{
    private const string ApiToken = "mlsn.not-a-real-token-0123456789";

    /// <summary>
    /// The complete set of top-level JSON properties the adapter is expected to
    /// send for a message with a reply-to.
    /// </summary>
    /// <remarks>
    /// Asserted as a set rather than field by field, so that a property added
    /// to the payload type — or one accidentally left in by a serialisation
    /// change — fails here instead of reaching MailerSend's validator.
    /// </remarks>
    private static readonly string[] ExpectedPayloadProperties =
        ["from", "to", "subject", "text", "html", "reply_to"];

    [Fact]
    public async Task PostsToMailerSendsDocumentedEndpointWithABearerToken()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Accepted);
        var sender = CreateSender(handler);

        await sender.SendAsync(TestMessages.Simple());

        var request = handler.Requests.ShouldHaveSingleItem();

        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri!.ToString().ShouldBe("https://api.mailersend.com/v1/email");
        request.AuthorizationScheme.ShouldBe("Bearer");
        request.AuthorizationParameter.ShouldBe(ApiToken);
        request.ContentType.ShouldStartWith("application/json");
    }

    [Fact]
    public async Task SerialisesTheRequestBodyInMailerSendsDocumentedShape()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Accepted);
        var sender = CreateSender(handler);

        var message = new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test", "SW5e"),
            to: EmailAddress.Create("player@example.com", "Jaina Solo"),
            subject: "Reset your SW5e password",
            plainTextBody: "Open https://sw5e.test/reset?token=abc&user=7 to continue.",
            htmlBody: "<p>Open <a href=\"https://sw5e.test/reset?token=abc&amp;user=7\">this link</a>.</p>",
            replyTo: EmailAddress.Create("support@sw5e.test"));

        await sender.SendAsync(message);

        using var document = JsonDocument.Parse(handler.Requests.ShouldHaveSingleItem().Body);
        var root = document.RootElement;

        root.EnumerateObject()
            .Select(property => property.Name)
            .ShouldBe(ExpectedPayloadProperties, ignoreOrder: true);

        // from is an object with email and name, not a formatted string.
        root.GetProperty("from").GetProperty("email").GetString().ShouldBe("noreply@sw5e.test");
        root.GetProperty("from").GetProperty("name").GetString().ShouldBe("SW5e");

        // to is an array even though this library only ever sends to one
        // recipient, because that is what the endpoint requires.
        var recipients = root.GetProperty("to");
        recipients.ValueKind.ShouldBe(JsonValueKind.Array);
        recipients.GetArrayLength().ShouldBe(1);
        recipients[0].GetProperty("email").GetString().ShouldBe("player@example.com");
        recipients[0].GetProperty("name").GetString().ShouldBe("Jaina Solo");

        root.GetProperty("subject").GetString().ShouldBe("Reset your SW5e password");

        // Both bodies go over verbatim. In particular the ampersand in the URL
        // survives as an ampersand: JSON string escaping is not HTML escaping,
        // and a link that arrives double-escaped is a link that does not work.
        root.GetProperty("text").GetString()
            .ShouldBe("Open https://sw5e.test/reset?token=abc&user=7 to continue.");
        root.GetProperty("html").GetString()
            .ShouldBe("<p>Open <a href=\"https://sw5e.test/reset?token=abc&amp;user=7\">this link</a>.</p>");

        root.GetProperty("reply_to").GetProperty("email").GetString().ShouldBe("support@sw5e.test");
    }

    /// <summary>
    /// MailerSend validates the shape of what it is sent, and an explicit null
    /// is not the same as an absent key.
    /// </summary>
    [Fact]
    public async Task OmitsOptionalPropertiesRatherThanSendingNull()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Accepted);
        var sender = CreateSender(handler);

        var message = new EmailMessage(
            from: EmailAddress.Create("noreply@sw5e.test"),
            to: EmailAddress.Create("player@example.com"),
            subject: "Confirm your SW5e email address",
            plainTextBody: "text",
            htmlBody: "<p>html</p>");

        await sender.SendAsync(message);

        var body = handler.Requests.ShouldHaveSingleItem().Body;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.TryGetProperty("reply_to", out _).ShouldBeFalse(
            "no reply-to was set, so the key must be absent rather than null");
        root.GetProperty("from").TryGetProperty("name", out _).ShouldBeFalse(
            "no display name was set, so the key must be absent rather than null");
        body.ShouldNotContain("null");
    }

    [Fact]
    public async Task ReportsTheMessageIdMailerSendReturns()
    {
        // 202 Accepted with an empty body and the handle in a header is exactly
        // what their documentation describes for a successful send.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.Accepted,
            configure: response => response.Headers.Add("x-message-id", "63e0f1b8c2a4d5e6f7a8b9c0"));

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();
        result.ProviderMessageId.ShouldBe("63e0f1b8c2a4d5e6f7a8b9c0");
    }

    [Fact]
    public async Task ReportsSuccessWithoutAMessageIdWhenTheHeaderIsAbsent()
    {
        var result = await CreateSender(StubHttpMessageHandler.Returning(HttpStatusCode.Accepted))
            .SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();
        result.ProviderMessageId.ShouldBeNull();
    }

    /// <summary>
    /// The classification that the retry decorator runs on. Each of these codes
    /// is one MailerSend documents for this endpoint.
    /// </summary>
    public static TheoryData<int, EmailFailureKind> StatusClassifications() => new()
    {
        { 400, EmailFailureKind.Permanent },   // malformed request
        { 401, EmailFailureKind.Permanent },   // bad or missing token
        { 403, EmailFailureKind.Permanent },   // token lacks the sending scope
        { 404, EmailFailureKind.Permanent },
        { 405, EmailFailureKind.Permanent },
        { 422, EmailFailureKind.Permanent },   // validation: unverified domain, bad address
        { 408, EmailFailureKind.Transient },   // request timeout
        { 421, EmailFailureKind.Transient },   // MailerSend's maintenance code
        { 429, EmailFailureKind.Transient },   // rate limited
        { 500, EmailFailureKind.Transient },
        { 502, EmailFailureKind.Transient },
        { 503, EmailFailureKind.Transient },
        { 504, EmailFailureKind.Transient },
    };

    [Theory]
    [MemberData(nameof(StatusClassifications))]
    public async Task ClassifiesEachStatusCodeMailerSendCanReturn(int status, EmailFailureKind expected)
    {
        var handler = StubHttpMessageHandler.Returning((HttpStatusCode)status, "{\"message\":\"nope\"}");

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(expected);
        result.Failure.Reason.ShouldContain(status.ToString());
    }

    /// <summary>
    /// A rate limit is only useful if the number attached to it survives up to
    /// the retry decorator, which is the only thing that can act on it.
    /// </summary>
    [Fact]
    public async Task PassesUpTheRetryAfterMailerSendSendsWithARateLimit()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.TooManyRequests,
            "{\"message\":\"Your account reached its rate limit of 120 requests/min. #MS42903\"}",
            configure: response => response.Headers.Add("retry-after", "59"));

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Failure!.Kind.ShouldBe(EmailFailureKind.Transient);
        result.Failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(59));
    }

    [Fact]
    public async Task ReportsNoRetryAfterWhenTheProviderSendsNone()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable);

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Failure!.RetryAfter.ShouldBeNull();
    }

    /// <summary>
    /// MailerSend's 422 body carries the field path and an <c>#MSxxxxx</c> code
    /// which is the first thing their support will ask for, so it has to reach
    /// the log.
    /// </summary>
    [Fact]
    public async Task QuotesTheProviderErrorBodyInTheFailureReason()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.UnprocessableEntity,
            """
            {"message":"The given data was invalid.","errors":{"from.email":["The from.email must be verified. #MS42207"]}}
            """);

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Failure!.Kind.ShouldBe(EmailFailureKind.Permanent);
        result.Failure.Reason.ShouldContain("#MS42207");
        result.Failure.Reason.ShouldContain("from.email");
    }

    /// <summary>
    /// The failure reason is written to the application log, which reaches a
    /// far wider audience than a sending credential ever should.
    /// </summary>
    [Fact]
    public async Task NeverRepeatsTheApiTokenIntoTheFailureReason()
    {
        // Simulates a provider that echoes the request back in a diagnostic
        // body. MailerSend does not, but the log entry must not become a
        // credential disclosure if any provider ever did.
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.Unauthorized,
            $$"""{"message":"Unauthenticated. Token was: {{ApiToken}}"}""");

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Failure!.Reason.ShouldNotContain(ApiToken);
        result.Failure.Reason.ShouldContain("[redacted]");
    }

    /// <summary>
    /// A remote string containing CRLF can otherwise forge a second, entirely
    /// fictitious log entry.
    /// </summary>
    [Fact]
    public async Task FlattensLineBreaksOutOfTheQuotedErrorBody()
    {
        var handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.BadRequest,
            "first line\r\nWARN Everything is fine, ignore the error above");

        var result = await CreateSender(handler).SendAsync(TestMessages.Simple());

        result.Failure!.Reason.ShouldNotContain("\n");
        result.Failure.Reason.ShouldNotContain("\r");
        result.Failure.Reason.ShouldContain("first line WARN Everything is fine");
    }

    [Fact]
    public async Task TreatsAnUnreachableProviderAsTransient()
    {
        var sender = CreateSender(new ThrowingHandler(
            new HttpRequestException("No such host is known.")));

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Failure!.Kind.ShouldBe(EmailFailureKind.Transient);
        result.Failure.Reason.ShouldContain("No such host is known.");
    }

    [Fact]
    public async Task TreatsTheClientTimeoutAsTransientRatherThanAsCancellation()
    {
        using var client = new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri(MailerSendOptions.DefaultBaseAddress),
            Timeout = TimeSpan.FromMilliseconds(100),
        };

        var sender = CreateSender(client);

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(EmailFailureKind.Transient);
        result.Failure.Reason.ShouldContain("did not respond");
    }

    /// <summary>
    /// A caller that gave up is not a delivery failure, and turning it into one
    /// would have the retry decorator dutifully retrying a request nobody is
    /// waiting for.
    /// </summary>
    [Fact]
    public async Task PropagatesCancellationFromTheCallersToken()
    {
        using var client = new HttpClient(new HangingHandler())
        {
            BaseAddress = new Uri(MailerSendOptions.DefaultBaseAddress),
        };

        var sender = CreateSender(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(
            () => sender.SendAsync(TestMessages.Simple(), cancellation.Token));
    }

    [Fact]
    public async Task ResolvesItsClientByTheNameRegistrationConfigures()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Returning(HttpStatusCode.Accepted))
        {
            BaseAddress = new Uri(MailerSendOptions.DefaultBaseAddress),
        };

        var factory = new SingleClientHttpClientFactory(client);

        var sender = new MailerSendEmailSender(
            factory,
            TestOptions.For(new MailerSendOptions { ApiToken = ApiToken }),
            NullLogger<MailerSendEmailSender>.Instance);

        await sender.SendAsync(TestMessages.Simple());

        // If these two ever drift apart the adapter silently gets a default
        // client with no base address and a hundred-second timeout.
        factory.RequestedName.ShouldBe(MailerSendEmailSender.HttpClientName);
    }

    private static MailerSendEmailSender CreateSender(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(MailerSendOptions.DefaultBaseAddress),
        };

        return CreateSender(client);
    }

    private static MailerSendEmailSender CreateSender(HttpClient client) =>
        new(
            new SingleClientHttpClientFactory(client),
            TestOptions.For(new MailerSendOptions { ApiToken = ApiToken }),
            NullLogger<MailerSendEmailSender>.Instance);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exception);
    }

    /// <summary>Never answers, so the client's own timeout is what fires.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
