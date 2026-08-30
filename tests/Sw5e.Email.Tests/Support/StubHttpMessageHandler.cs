using System.Collections.Concurrent;
using System.Net;

namespace Sw5e.Email.Tests.Support;

/// <summary>
/// One HTTP request, captured in full before the adapter disposed it.
/// </summary>
/// <remarks>
/// The body is read eagerly by the handler. An <see cref="HttpRequestMessage"/>
/// held past the call has already had its content stream consumed and disposed,
/// so a test that kept the request object and read it afterwards would assert
/// on nothing.
/// </remarks>
internal sealed record CapturedHttpRequest(
    HttpMethod Method,
    Uri? Uri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? ContentType,
    string Body);

/// <summary>
/// Stands in for MailerSend at the transport boundary.
/// </summary>
/// <remarks>
/// Deliberately placed at the lowest seam available — a
/// <see cref="HttpMessageHandler"/> under a real <see cref="HttpClient"/> —
/// rather than by faking the adapter or the client. Everything above it is the
/// production code path: real request construction, real JSON serialisation,
/// real header handling, real response parsing. What a test asserts here is
/// the actual bytes MailerSend would receive.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<CapturedHttpRequest, int, HttpResponseMessage> _respond;
    private readonly ConcurrentQueue<CapturedHttpRequest> _requests = new();
    private int _callCount;

    /// <param name="respond">
    /// Produces the response for a captured request and its one-based attempt
    /// number. The attempt number is what lets a test make the first call fail
    /// and the second succeed.
    /// </param>
    public StubHttpMessageHandler(Func<CapturedHttpRequest, int, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>Every request made, in order.</summary>
    public IReadOnlyList<CapturedHttpRequest> Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var captured = new CapturedHttpRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Content?.Headers.ContentType?.ToString(),
            body);

        _requests.Enqueue(captured);

        return _respond(captured, Interlocked.Increment(ref _callCount));
    }

    /// <summary>Always answers with the same status and body.</summary>
    public static StubHttpMessageHandler Returning(
        HttpStatusCode status,
        string body = "",
        Action<HttpResponseMessage>? configure = null) =>
        new((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };

            configure?.Invoke(response);
            return response;
        });
}
