using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sw5e.Email.Resilience;

/// <summary>
/// Retries transient send failures. A decorator over any
/// <see cref="IEmailSender"/>, never a provider in its own right.
/// </summary>
/// <remarks>
/// <para>
/// Resilience sits here, above the seam, for the same reason the templates do:
/// so that it is written once. A retry loop inside the MailerSend adapter
/// would understand HTTP status codes and would have to be rewritten, in SMTP
/// reply codes, for the next provider — and the third provider would get a
/// third variant with its own subtly different backoff. Expressing the policy
/// against <see cref="EmailFailureKind"/> means every adapter contributes one
/// thing, a correct classification, and inherits the rest.
/// </para>
/// <para>
/// What it will not do:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Retry a permanent failure.</b> An unverified sending domain or a
///     rejected credential fails identically every time; repeating it turns one
///     clear error into four and delays the caller learning the truth.
///   </description></item>
///   <item><description>
///     <b>Retry a cancellation.</b> <see cref="OperationCanceledException"/>
///     propagates untouched. The caller has already gone.
///   </description></item>
///   <item><description>
///     <b>Wait longer than the budget.</b> If a provider asks for more time
///     than <see cref="EmailRetryOptions.MaxDelay"/>, the send is abandoned as
///     transient rather than parking a request thread.
///   </description></item>
/// </list>
/// <para>
/// One thing it cannot do anything about: a send that reaches the provider and
/// then fails on the way back — a timeout after the message was accepted, say.
/// Retrying that delivers the email twice. Two verification emails is a far
/// better outcome than none, so the policy is deliberate, but it is a policy
/// and not an oversight. A future outbox with idempotency keys is where that
/// gets solved properly.
/// </para>
/// </remarks>
public sealed class RetryingEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;
    private readonly IOptions<EmailRetryOptions> _options;
    private readonly ILogger<RetryingEmailSender> _logger;

    /// <summary>
    /// How the decorator waits.
    /// </summary>
    /// <remarks>
    /// A seam so that tests can assert the exact backoff schedule instead of
    /// sleeping through it. A test that really waited would be slow, and — far
    /// worse — would have to assert the delay loosely enough to survive a busy
    /// build agent, which is another way of saying it would pass whether or not
    /// the backoff were correct.
    /// </remarks>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// The jitter source, returning a value in <c>[0, 1)</c>. Injectable for
    /// the same reason as <see cref="_delay"/>: a randomised schedule that
    /// cannot be pinned cannot be asserted.
    /// </summary>
    private readonly Func<double> _jitter;

    /// <summary>Creates the decorator.</summary>
    public RetryingEmailSender(
        IEmailSender inner,
        IOptions<EmailRetryOptions> options,
        ILogger<RetryingEmailSender> logger)
        : this(inner, options, logger, Task.Delay, Random.Shared.NextDouble)
    {
    }

    internal RetryingEmailSender(
        IEmailSender inner,
        IOptions<EmailRetryOptions> options,
        ILogger<RetryingEmailSender> logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<double> jitter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _options = options;
        _logger = logger;
        _delay = delay;
        _jitter = jitter;
    }

    /// <summary>The sender being decorated. Exposed so registration is verifiable.</summary>
    public IEmailSender Inner => _inner;

    /// <inheritdoc />
    public async Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _options.Value;
        var maxAttempts = Math.Max(1, options.MaxAttempts);

        for (var attempt = 1; ; attempt++)
        {
            var result = await _inner.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                if (attempt > 1)
                {
                    // Worth an Information line: a send that only worked on the
                    // third try is invisible otherwise, and a rising count of
                    // these is the early warning that a provider is degrading.
                    _logger.LogInformation(
                        "Delivery to {Recipient} succeeded on attempt {Attempt} of {MaxAttempts}.",
                        message.To.Address,
                        attempt,
                        maxAttempts);
                }

                return result;
            }

            var failure = result.Failure!;

            if (failure.Kind is not EmailFailureKind.Transient)
            {
                return result;
            }

            if (attempt >= maxAttempts)
            {
                _logger.LogError(
                    "Delivery to {Recipient} failed after {Attempt} attempts and will not be " +
                    "retried. {Reason}",
                    message.To.Address,
                    attempt,
                    failure.Reason);

                return result;
            }

            var delay = NextDelay(attempt, failure.RetryAfter, options);

            if (delay is null)
            {
                _logger.LogWarning(
                    "Delivery to {Recipient} was deferred by the provider for longer than the " +
                    "{MaxDelay} retry budget, so it was abandoned after attempt {Attempt}. " +
                    "{Reason}",
                    message.To.Address,
                    options.MaxDelay,
                    attempt,
                    failure.Reason);

                return result;
            }

            _logger.LogWarning(
                "Delivery to {Recipient} failed on attempt {Attempt} of {MaxAttempts}; " +
                "retrying in {Delay}. {Reason}",
                message.To.Address,
                attempt,
                maxAttempts,
                delay.Value,
                failure.Reason);

            await _delay(delay.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long to wait before <paramref name="attempt"/> + 1, or null when the
    /// send should be abandoned instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A provider that states a wait is obeyed exactly, with no jitter added.
    /// It knows when its own rate-limit window reopens and guessing around that
    /// number only produces another rejection. If the number exceeds the
    /// budget, null.
    /// </para>
    /// <para>
    /// Otherwise the wait is exponential — doubling from
    /// <see cref="EmailRetryOptions.InitialDelay"/>, capped at
    /// <see cref="EmailRetryOptions.MaxDelay"/> — with equal jitter applied:
    /// half the computed delay, plus a random share of the other half. The
    /// randomness is not decoration. Every instance of this application behind
    /// a load balancer sees a provider outage at the same moment; a fixed
    /// schedule would have them all retry in the same millisecond, which is a
    /// synchronised burst arriving exactly as the provider is trying to
    /// recover. Spreading the retries out is the difference between helping it
    /// recover and re-breaking it.
    /// </para>
    /// <para>
    /// The exponent is computed in floating point and capped before conversion,
    /// so a large <see cref="EmailRetryOptions.MaxAttempts"/> cannot overflow
    /// the way a <c>1 &lt;&lt; attempt</c> in ticks would.
    /// </para>
    /// </remarks>
    private TimeSpan? NextDelay(int attempt, TimeSpan? retryAfter, EmailRetryOptions options)
    {
        if (retryAfter is { } requested)
        {
            return requested > options.MaxDelay ? null : requested;
        }

        var initial = options.InitialDelay > TimeSpan.Zero
            ? options.InitialDelay.TotalMilliseconds
            : 0d;

        var exponential = initial * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, options.MaxDelay.TotalMilliseconds);

        // Equal jitter: never shorter than half the computed delay, never
        // longer than the whole of it. Full jitter (anywhere in [0, delay])
        // would occasionally produce a near-zero wait, which is the one
        // outcome an overloaded provider least needs.
        var jittered = (capped / 2d) + (capped / 2d * _jitter());

        return TimeSpan.FromMilliseconds(jittered);
    }
}
