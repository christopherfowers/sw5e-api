using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Sw5e.Email.Tests.Support;

/// <summary>Wraps a value as <see cref="IOptions{TOptions}"/>.</summary>
internal static class TestOptions
{
    public static IOptions<T> For<T>(T value) where T : class => Options.Create(value);
}

/// <summary>
/// An <see cref="IEmailSender"/> that returns a scripted sequence of results.
/// </summary>
/// <remarks>
/// Used only to drive the retry decorator, which is the one place where a
/// scripted inner is the right tool: the decorator's whole job is deciding what
/// to do with a sequence of outcomes, so a test of it has to be able to state
/// the sequence. Note that the assertions built on this are about the
/// decorator's observable behaviour — how many attempts it made, how long it
/// waited between them — and never merely that it called something.
/// </remarks>
internal sealed class ScriptedEmailSender : IEmailSender
{
    private readonly Queue<EmailDeliveryResult> _results;
    private readonly EmailDeliveryResult _afterScript;

    public ScriptedEmailSender(
        IEnumerable<EmailDeliveryResult> results,
        EmailDeliveryResult? afterScript = null)
    {
        _results = new Queue<EmailDeliveryResult>(results);

        // What to return once the script is exhausted. Defaults to a transient
        // failure so that a decorator retrying more than the script expects
        // keeps failing rather than accidentally succeeding.
        _afterScript = afterScript
            ?? EmailDeliveryResult.Transient("the scripted sequence was exhausted");
    }

    public int Attempts { get; private set; }

    public List<EmailMessage> Received { get; } = [];

    public Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        Attempts++;
        Received.Add(message);

        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : _afterScript);
    }
}

/// <summary>
/// A minimal <see cref="IHostEnvironment"/>, so the Development fallback can be
/// tested without standing up a host.
/// </summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; } = "Sw5e.Email.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } =
        new NullFileProvider();
}

/// <summary>
/// Hands out one prepared <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// The adapter resolves its client through <see cref="IHttpClientFactory"/>, so
/// the test supplies a factory rather than the client. That keeps the
/// production resolution path intact while letting the test own the handler
/// underneath it.
/// </remarks>
internal sealed class SingleClientHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public SingleClientHttpClientFactory(HttpClient client) => _client = client;

    public string? RequestedName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        RequestedName = name;
        return _client;
    }
}

/// <summary>Message fixtures, so each test states only what it varies.</summary>
internal static class TestMessages
{
    public static EmailAddress Sender => EmailAddress.Create("noreply@sw5e.test", "SW5e");

    public static EmailAddress Recipient => EmailAddress.Create("player@example.com", "Jaina Solo");

    public static EmailMessage Simple() => new(
        from: Sender,
        to: Recipient,
        subject: "Confirm your SW5e email address",
        plainTextBody: "Open https://sw5e.test/verify?token=abc to confirm.",
        htmlBody: "<p>Open <a href=\"https://sw5e.test/verify?token=abc\">this link</a>.</p>");
}
