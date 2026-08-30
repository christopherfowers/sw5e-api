using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Sw5e.Email.Resilience;
using Sw5e.Email.Tests.Support;

namespace Sw5e.Email.Tests;

/// <summary>
/// Tests the retry decorator by pinning its two sources of non-determinism —
/// the clock and the jitter — and then asserting the exact schedule it
/// produces.
/// </summary>
/// <remarks>
/// The schedule is the behaviour. A test that only counted attempts would pass
/// against a decorator that retried in a tight loop with no backoff at all,
/// which is the failure mode that turns a provider's brief wobble into a
/// self-inflicted denial of service.
/// </remarks>
public sealed class RetryingEmailSenderTests
{
    /// <summary>
    /// Jitter pinned to its maximum, so the recorded delay equals the computed
    /// exponential delay exactly.
    /// </summary>
    private const double NoJitter = 1.0;

    [Fact]
    public async Task RetriesATransientFailureAndReturnsTheEventualSuccess()
    {
        var inner = new ScriptedEmailSender([
            EmailDeliveryResult.Transient("503"),
            EmailDeliveryResult.Transient("503"),
            EmailDeliveryResult.Success("accepted-at-last"),
        ]);

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions
        {
            MaxAttempts = 4,
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(5),
        });

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();
        result.ProviderMessageId.ShouldBe("accepted-at-last");
        inner.Attempts.ShouldBe(3);

        // Doubling from the configured initial delay, and no wait after the
        // attempt that worked.
        delays.ShouldBe([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)]);
    }

    [Fact]
    public async Task DoesNotRetryAPermanentFailure()
    {
        var inner = new ScriptedEmailSender(
            [EmailDeliveryResult.Permanent("422 the sending domain is not verified")],
            afterScript: EmailDeliveryResult.Success());

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions());

        var result = await sender.SendAsync(TestMessages.Simple());

        // The afterScript success is the trap: a decorator that retried would
        // report success and hide a configuration error that will never fix
        // itself.
        result.Succeeded.ShouldBeFalse();
        result.Failure!.Reason.ShouldBe("422 the sending domain is not verified");
        inner.Attempts.ShouldBe(1);
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task StopsAtTheAttemptLimitAndReturnsTheLastFailure()
    {
        var inner = new ScriptedEmailSender([]);

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(5),
        });

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Kind.ShouldBe(EmailFailureKind.Transient);
        inner.Attempts.ShouldBe(3);

        // Two waits for three attempts: the decorator must not sleep after the
        // final one, which nobody is waiting on.
        delays.ShouldBe([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)]);
    }

    [Fact]
    public async Task SendsOnceWhenRetryingIsDisabledByTheAttemptLimit()
    {
        var inner = new ScriptedEmailSender([]);

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions { MaxAttempts = 1 });

        await sender.SendAsync(TestMessages.Simple());

        inner.Attempts.ShouldBe(1);
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task CapsTheExponentialBackoffAtTheConfiguredMaximum()
    {
        var inner = new ScriptedEmailSender([]);

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions
        {
            MaxAttempts = 6,
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(2),
        });

        await sender.SendAsync(TestMessages.Simple());

        // 500, 1000, then flat at the 2000 ceiling rather than 2000, 4000, 8000.
        delays.ShouldBe([
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromMilliseconds(2000),
            TimeSpan.FromMilliseconds(2000),
            TimeSpan.FromMilliseconds(2000),
        ]);
    }

    /// <summary>
    /// Equal jitter: never less than half the computed delay, never more than
    /// all of it. The lower bound is the half that matters — full jitter would
    /// sometimes produce a near-instant retry against a provider that is
    /// already struggling.
    /// </summary>
    [Fact]
    public async Task SpreadsRetriesOverTheLowerHalfOfTheWindowWhenJitterIsAtItsMinimum()
    {
        var inner = new ScriptedEmailSender([]);

        var (sender, delays) = CreateSender(
            inner,
            new EmailRetryOptions
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
            },
            jitter: 0.0);

        await sender.SendAsync(TestMessages.Simple());

        delays.ShouldBe([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)]);
    }

    /// <summary>
    /// The provider knows when its own rate-limit window reopens. Guessing
    /// around that number only earns another rejection.
    /// </summary>
    [Fact]
    public async Task ObeysAProviderSuppliedRetryAfterInsteadOfItsOwnBackoff()
    {
        var inner = new ScriptedEmailSender([
            EmailDeliveryResult.Transient("429", TimeSpan.FromSeconds(2)),
            EmailDeliveryResult.Success(),
        ]);

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions
        {
            MaxAttempts = 4,
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(5),
        });

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeTrue();

        // Exactly two seconds: the provider's number, neither the 500ms
        // exponential value nor a jittered version of either.
        delays.ShouldBe([TimeSpan.FromSeconds(2)]);
    }

    /// <summary>
    /// MailerSend answers a rate-limited request with <c>retry-after: 59</c>.
    /// Sleeping for a minute inside an HTTP request would exhaust the thread
    /// pool and trip every timeout upstream, so the send is abandoned and the
    /// caller gets to decide.
    /// </summary>
    [Fact]
    public async Task AbandonsTheSendWhenTheProviderAsksForLongerThanTheBudget()
    {
        var inner = new ScriptedEmailSender([
            EmailDeliveryResult.Transient("429 rate limited", TimeSpan.FromSeconds(59)),
            EmailDeliveryResult.Success(),
        ]);

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions
        {
            MaxAttempts = 4,
            InitialDelay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(5),
        });

        var result = await sender.SendAsync(TestMessages.Simple());

        result.Succeeded.ShouldBeFalse();
        result.Failure!.Reason.ShouldBe("429 rate limited");
        inner.Attempts.ShouldBe(1);
        delays.ShouldBeEmpty("waiting 59 seconds inside a request is not resilience");
    }

    [Fact]
    public async Task DoesNotRetryACancelledSend()
    {
        var inner = new CancellingEmailSender();

        var (sender, delays) = CreateSender(inner, new EmailRetryOptions { MaxAttempts = 4 });

        await Should.ThrowAsync<OperationCanceledException>(
            () => sender.SendAsync(TestMessages.Simple()));

        inner.Attempts.ShouldBe(1);
        delays.ShouldBeEmpty();
    }

    [Fact]
    public async Task PassesTheSameMessageToEveryAttempt()
    {
        var inner = new ScriptedEmailSender([
            EmailDeliveryResult.Transient("503"),
            EmailDeliveryResult.Success(),
        ]);

        var (sender, _) = CreateSender(inner, new EmailRetryOptions());
        var message = TestMessages.Simple();

        await sender.SendAsync(message);

        inner.Received.ShouldAllBe(received => ReferenceEquals(received, message));
    }

    private static (RetryingEmailSender Sender, List<TimeSpan> Delays) CreateSender(
        IEmailSender inner,
        EmailRetryOptions options,
        double jitter = NoJitter)
    {
        var delays = new List<TimeSpan>();

        var sender = new RetryingEmailSender(
            inner,
            TestOptions.For(options),
            NullLogger<RetryingEmailSender>.Instance,
            delay: (waited, _) =>
            {
                delays.Add(waited);
                return Task.CompletedTask;
            },
            jitter: () => jitter);

        return (sender, delays);
    }

    private sealed class CancellingEmailSender : IEmailSender
    {
        public int Attempts { get; private set; }

        public Task<EmailDeliveryResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new OperationCanceledException();
        }
    }
}
