using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sw5e.Email.Providers.MailerSend;

/// <summary>
/// Sends through MailerSend's HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// The whole integration is one call: <c>POST /v1/email</c> with a bearer
/// token, answered with <c>202 Accepted</c> and an <c>x-message-id</c> header.
/// Everything else in this file is turning their failure vocabulary into the
/// two answers the rest of the system understands.
/// </para>
/// <para>
/// No retry logic lives here. Retrying is a decorator applied above
/// <see cref="IEmailSender"/> — see <c>RetryingEmailSender</c> — so this type
/// only has to classify a failure correctly, and every other provider gets the
/// same retry behaviour without reimplementing it.
/// </para>
/// </remarks>
public sealed class MailerSendEmailSender : IEmailSender
{
    /// <summary>
    /// The named <see cref="HttpClient"/> registration this adapter resolves.
    /// </summary>
    /// <remarks>
    /// Named rather than typed so the base address, timeout and handler
    /// lifetime are configured in one visible place at registration, and so a
    /// test can replace the primary handler without replacing the adapter.
    /// </remarks>
    public const string HttpClientName = "Sw5e.Email.MailerSend";

    /// <summary>The send endpoint, relative to the configured base address.</summary>
    private const string SendPath = "v1/email";

    /// <summary>
    /// How much of a provider error body is quoted into a failure reason.
    /// </summary>
    /// <remarks>
    /// Enough to carry MailerSend's <c>message</c> field and their <c>#MSxxxxx</c>
    /// error code, which is what support will ask for. Bounded because the
    /// reason ends up in a log line, and an unbounded remote string in a log
    /// line is someone else's decision about how big this application's logs
    /// get.
    /// </remarks>
    private const int MaxQuotedBodyLength = 512;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<MailerSendOptions> _options;
    private readonly ILogger<MailerSendEmailSender> _logger;

    /// <summary>Creates the adapter.</summary>
    public MailerSendEmailSender(
        IHttpClientFactory httpClientFactory,
        IOptions<MailerSendOptions> options,
        ILogger<MailerSendEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.Value;
        var payload = BuildPayload(message);

        using var request = new HttpRequestMessage(HttpMethod.Post, SendPath)
        {
            Content = JsonContent.Create(payload, options: MailerSendSerialization.Options),
        };

        // Set per request rather than baked into the client's default headers.
        // A token that lives on the client outlives any single call and is one
        // careless log-the-client away from disclosure; setting it here keeps
        // its lifetime as short as the request it authorises.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, so this is not a delivery failure and must
            // not be retried. Distinguishing this from the timeout below is the
            // reason for the `when` clause: HttpClient reports its own timeout
            // as the same exception type.
            throw;
        }
        catch (TaskCanceledException exception)
        {
            // HttpClient.Timeout elapsed. Transient by definition — the request
            // may well have been fine and the provider merely slow.
            _logger.LogWarning(
                exception,
                "MailerSend did not respond within {Timeout} for {Recipient}.",
                client.Timeout,
                message.To.Address);

            return EmailDeliveryResult.Transient(
                $"MailerSend did not respond within {client.Timeout}.");
        }
        catch (HttpRequestException exception)
        {
            // DNS failure, connection refused, TLS failure, connection reset.
            // All of these are the network being the network.
            _logger.LogWarning(
                exception,
                "MailerSend was unreachable while sending to {Recipient}.",
                message.To.Address);

            return EmailDeliveryResult.Transient(
                $"MailerSend was unreachable: {exception.Message}");
        }

        using (response)
        {
            return await InterpretAsync(response, message, options, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Maps the provider-neutral message onto MailerSend's request body.
    /// </summary>
    /// <remarks>
    /// The only translation step in the adapter, and the only place that knows
    /// MailerSend spells a mailbox <c>{ "email": …, "name": … }</c>.
    /// </remarks>
    private static MailerSendPayload BuildPayload(EmailMessage message) => new()
    {
        From = ToContact(message.From),
        To = [ToContact(message.To)],
        Subject = message.Subject,
        Text = message.PlainTextBody,
        Html = message.HtmlBody,
        ReplyTo = message.ReplyTo is null ? null : ToContact(message.ReplyTo),
    };

    private static MailerSendContact ToContact(EmailAddress address) =>
        new(address.Address, address.DisplayName);

    /// <summary>
    /// Turns an HTTP response into a delivery result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classification, and why each bucket is where it is:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>2xx</b> — accepted. Specifically <c>202</c>, with an empty body
    ///     and the message handle in <c>x-message-id</c>. Any other 2xx is
    ///     treated the same rather than being called an error, because a
    ///     provider adding a success code should not break sending.
    ///   </description></item>
    ///   <item><description>
    ///     <b>429</b> — rate limited. MailerSend allows 120 requests a minute
    ///     to this endpoint and returns <c>retry-after</c> in seconds. Purely a
    ///     matter of timing, so transient, and the header is passed up so the
    ///     retry decorator can obey it instead of guessing.
    ///   </description></item>
    ///   <item><description>
    ///     <b>408, 421, 425 and 5xx</b> — transient. 421 is the one worth
    ///     naming: MailerSend uses it for planned maintenance, which is exactly
    ///     the case retrying is for.
    ///   </description></item>
    ///   <item><description>
    ///     <b>401 and 403</b> — permanent, and separately logged as an error,
    ///     because these mean the token is missing, wrong, revoked or lacking
    ///     the sending scope. Retrying a rejected credential just spends the
    ///     retry budget on a certainty.
    ///   </description></item>
    ///   <item><description>
    ///     <b>422</b> — permanent. MailerSend's validation failure: an
    ///     unverified sending domain, a malformed recipient, an oversized body.
    ///     The same payload will fail identically every time.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Every other 4xx</b> — permanent, on the general principle that a
    ///     request the server calls wrong does not become right by repetition.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private async Task<EmailDeliveryResult> InterpretAsync(
        HttpResponseMessage response,
        EmailMessage message,
        MailerSendOptions options,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var messageId = response.Headers.TryGetValues("x-message-id", out var values)
                ? values.FirstOrDefault()
                : null;

            // Debug, not Information: this fires once per registration and once
            // per password reset, and at Information it would be pure noise in
            // a production log. The identifier is here so that when something
            // does go wrong it can be looked up in MailerSend's activity feed.
            _logger.LogDebug(
                "MailerSend accepted a message for {Recipient} as {ProviderMessageId}.",
                message.To.Address,
                messageId);

            return EmailDeliveryResult.Success(messageId);
        }

        var body = await ReadBodySnippetAsync(response, options, cancellationToken)
            .ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var reason = string.Format(
            CultureInfo.InvariantCulture,
            "MailerSend returned {0} ({1}). {2}",
            status,
            response.StatusCode,
            body);

        // Matched on the number rather than on HttpStatusCode members, because
        // 425 Too Early has no member in this framework version and a set of
        // codes half-named and half-numeric reads worse than one that is
        // consistently numeric.
        var transient = status switch
        {
            408 => true, // Request Timeout
            421 => true, // MailerSend uses this for planned maintenance
            425 => true, // Too Early
            429 => true, // Rate limited; retry-after says when
            _ => status >= 500,
        };

        if (transient)
        {
            var retryAfter = ReadRetryAfter(response);

            _logger.LogWarning(
                "MailerSend temporarily refused a message for {Recipient}. {Reason}",
                message.To.Address,
                reason);

            return EmailDeliveryResult.Transient(reason, retryAfter);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogError(
                "MailerSend rejected the configured API token. No mail can be sent until " +
                "Email__MailerSend__ApiToken is corrected. {Reason}",
                reason);
        }
        else
        {
            _logger.LogError(
                "MailerSend permanently rejected a message for {Recipient}. {Reason}",
                message.To.Address,
                reason);
        }

        return EmailDeliveryResult.Permanent(reason);
    }

    /// <summary>
    /// Reads the error body, bounded and stripped of anything unwelcome in a
    /// log line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things happen to a remote string before it is allowed near a log:
    /// it is truncated, its line breaks are flattened, and the API token is
    /// redacted out of it.
    /// </para>
    /// <para>
    /// The redaction is belt and braces — MailerSend does not echo the
    /// <c>Authorization</c> header back, and nothing here puts it in the body.
    /// It costs one comparison, and the failure it guards against is a bearer
    /// token in plain text in an aggregated log store, which is not a failure
    /// worth being clever about. A provider that starts echoing request
    /// headers in a diagnostic response should not be able to turn that into a
    /// credential disclosure here.
    /// </para>
    /// <para>
    /// Flattening line breaks is the log-forging guard: a remote string
    /// containing CRLF can otherwise fabricate an entire extra log entry.
    /// </para>
    /// </remarks>
    private static async Task<string> ReadBodySnippetAsync(
        HttpResponseMessage response,
        MailerSendOptions options,
        CancellationToken cancellationToken)
    {
        string body;

        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // The status code is the useful part and it is already in hand;
            // losing the body must not turn a classified failure into an
            // unhandled exception.
            return "(the response body could not be read)";
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return "(no response body)";
        }

        if (!string.IsNullOrEmpty(options.ApiToken))
        {
            body = body.Replace(options.ApiToken, "[redacted]", StringComparison.Ordinal);
        }

        body = body.ReplaceLineEndings(" ").Trim();

        return body.Length > MaxQuotedBodyLength
            ? string.Concat(body.AsSpan(0, MaxQuotedBodyLength), "…")
            : body;
    }

    /// <summary>
    /// Reads <c>retry-after</c>, which MailerSend sends as a whole number of
    /// seconds but which HTTP also permits as an absolute date.
    /// </summary>
    /// <remarks>
    /// A date in the past yields a negative delta; that is clamped to zero
    /// rather than handed upward, because a negative delay is not something the
    /// retry decorator should have to defend against.
    /// </remarks>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
