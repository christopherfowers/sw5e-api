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
