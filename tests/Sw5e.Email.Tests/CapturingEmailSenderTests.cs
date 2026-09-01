using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sw5e.Email.Providers.Capture;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the development and test provider.
/// </summary>
public sealed class CapturingEmailSenderTests
{
    /// <summary>
    /// The body never reaches the log, and the recipient and subject do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This provider's log line has always carried a comment promising that the
    /// body stays out of it, and until now nothing checked. The promise is
    /// load-bearing in a way it was not when it was written: message bodies
    /// used to carry verification links, which are bearer credentials, and they
    /// now also carry sign-in codes, which are credentials somebody can read
    /// off a screen and type. A log line containing one is a credential store
    /// with no access control, shipped to wherever logs are shipped.
    /// </para>
    /// <para>
    /// Asserted in both directions. Checking only that the body is absent would
    /// pass against a provider that logged nothing at all, which would be a
    /// different bug — a developer running the capture provider needs to see
    /// that a message was produced and who it was for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LogsTheRecipientAndSubjectAndNeverTheBody()
    {
        var log = new RecordingLogger<CapturingEmailSender>();
        var sender = new CapturingEmailSender(log);

        await sender.SendAsync(new EmailMessage(
            from: TestMessages.Sender,
            to: TestMessages.Recipient,
            subject: "Your SW5e sign-in code",
            plainTextBody: "Your sign-in code is 481625",
            htmlBody: "<p>Your sign-in code is 481625</p>"));

        var written = string.Join(Environment.NewLine, log.Messages);

        written.ShouldContain(TestMessages.Recipient.Address);
        written.ShouldContain("Your SW5e sign-in code");

        written.ShouldNotContain("481625");
        written.ShouldNotContain("Your sign-in code is");
        written.ShouldNotContain("<p>");
    }

    [Fact]
    public async Task KeepsTheWholeMessageRatherThanACallCount()
    {
        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);
        var message = TestMessages.Simple();

        var result = await sender.SendAsync(message);

        result.Succeeded.ShouldBeTrue();
        sender.Sent.ShouldHaveSingleItem().Message.ShouldBeSameAs(message);
    }

    [Fact]
    public async Task RetainsMessagesInTheOrderTheyWereSent()
    {
        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await sender.SendAsync(new EmailMessage(
                from: TestMessages.Sender,
                to: TestMessages.Recipient,
                subject: $"Message {i}",
                plainTextBody: "text",
                htmlBody: "<p>html</p>"));
        }

        sender.Sent.Select(captured => captured.Message.Subject)
            .ShouldBe(["Message 0", "Message 1", "Message 2"]);
    }

    [Fact]
    public async Task ForgetsEverythingWhenCleared()
    {
        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);

        await sender.SendAsync(TestMessages.Simple());
        sender.Clear();

        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// This is a singleton that would otherwise grow for as long as the process
    /// runs.
    /// </summary>
    [Fact]
    public async Task DiscardsTheOldestOnceTheRetentionBoundIsReached()
    {
        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);
        var overflow = CapturingEmailSender.MaxRetained + 10;

        for (var i = 0; i < overflow; i++)
        {
            await sender.SendAsync(new EmailMessage(
                from: TestMessages.Sender,
                to: TestMessages.Recipient,
                subject: $"Message {i}",
                plainTextBody: "text",
                htmlBody: "<p>html</p>"));
        }

        sender.Sent.Count.ShouldBe(CapturingEmailSender.MaxRetained);

        // The most recent survive; the first ten are the ones dropped.
        sender.Sent[0].Message.Subject.ShouldBe("Message 10");
        sender.Sent[^1].Message.Subject.ShouldBe($"Message {overflow - 1}");
    }

    [Fact]
    public async Task StampsEachCaptureFromTheInjectedClock()
    {
        var clock = new FixedTimeProvider(
            new DateTimeOffset(2026, 3, 14, 15, 9, 26, TimeSpan.Zero));

        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance, clock);

        await sender.SendAsync(TestMessages.Simple());

        sender.Sent.ShouldHaveSingleItem().CapturedAt.ShouldBe(clock.Now);
    }

    /// <summary>
    /// A test that cancels should see the same behaviour whichever provider is
    /// configured, or the capture provider stops being a faithful stand-in.
    /// </summary>
    [Fact]
    public async Task RefusesAnAlreadyCancelledSend()
    {
        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sender.SendAsync(TestMessages.Simple(), cancellation.Token));

        sender.Sent.ShouldBeEmpty();
    }

    /// <summary>
    /// The whole point of a singleton provider is that concurrent requests
    /// share it.
    /// </summary>
    [Fact]
    public async Task LosesNothingWhenSendsOverlap()
    {
        var sender = new CapturingEmailSender(NullLogger<CapturingEmailSender>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => sender.SendAsync(
            new EmailMessage(
                from: TestMessages.Sender,
                to: TestMessages.Recipient,
                subject: $"Message {i}",
                plainTextBody: "text",
                htmlBody: "<p>html</p>"))));

        sender.Sent.Count.ShouldBe(100);
        sender.Sent.Select(captured => captured.Message.Subject).Distinct().Count().ShouldBe(100);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public FixedTimeProvider(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps what it was asked to write.
/// </summary>
/// <remarks>
/// Formats each entry the way a real provider would, so an assertion about what
/// does and does not appear in a log is an assertion about the text an operator
/// would actually see rather than about the structured arguments behind it.
/// </remarks>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
