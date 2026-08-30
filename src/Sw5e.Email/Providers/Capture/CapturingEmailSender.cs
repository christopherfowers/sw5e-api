using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Sw5e.Email.Providers.Capture;

/// <summary>A message that was captured instead of sent.</summary>
/// <param name="Message">The message, exactly as it would have gone out.</param>
/// <param name="CapturedAt">When it was captured.</param>
public sealed record CapturedEmail(EmailMessage Message, DateTimeOffset CapturedAt);

/// <summary>
/// Records messages rather than sending them.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs, and they are the same job.
/// </para>
/// <para>
/// <b>Development.</b> The application starts and every account flow runs end
/// to end with no MailerSend account, no API token and no relay. The
/// alternative — real credentials on developer machines — means a shared token
/// in a chat log within a fortnight, and real mail to real strangers whenever
/// someone types a plausible address into a test form.
/// </para>
/// <para>
/// It logs the recipient and subject of each message and deliberately not the
/// body; see the note in <see cref="SendAsync"/>. To actually open a
/// verification link locally, point <c>Email:Provider</c> at <c>Smtp</c> and run
/// a catcher such as Mailpit on loopback — the SMTP adapter's cleartext
/// allowance for loopback hosts exists for exactly that.
/// </para>
/// <para>
/// <b>Tests.</b> <see cref="Sent"/> is what a test asserts against. Note what
/// this does <i>not</i> license: asserting that this class was called proves
/// only that this class was called. The useful assertions are about content —
/// that the reset link reached both body parts, that a display name containing
/// markup arrived escaped — and those are properties of the message, which is
/// why the whole message is kept rather than a call count.
/// </para>
/// <para>
/// Safe for concurrent use.
/// </para>
/// </remarks>
public sealed class CapturingEmailSender : IEmailSender
{
    /// <summary>
    /// How many messages are retained before the oldest are discarded.
    /// </summary>
    /// <remarks>
    /// A bound exists because this is a singleton that would otherwise grow for
    /// as long as the process runs — a slow leak in a development session, and
    /// a real one in any environment where someone selects this provider and
    /// forgets. Far more than any test needs and far more than a developer will
    /// scroll back through.
    /// </remarks>
    public const int MaxRetained = 500;

    private readonly ConcurrentQueue<CapturedEmail> _sent = new();
    private readonly ILogger<CapturingEmailSender> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the capturing sender.</summary>
    /// <param name="logger">Where captured messages are written for a developer to read.</param>
    /// <param name="timeProvider">
    /// The clock. Injected so a test can assert on capture ordering without
    /// depending on wall-clock resolution.
    /// </param>
    public CapturingEmailSender(
        ILogger<CapturingEmailSender> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Everything captured so far, oldest first.
    /// </summary>
    /// <remarks>
    /// A snapshot. Enumerating it while another thread sends will not throw,
    /// and will not observe the concurrent send either.
    /// </remarks>
    public IReadOnlyList<CapturedEmail> Sent => [.. _sent];

    /// <summary>Discards everything captured so far.</summary>
    /// <remarks>
    /// For tests that share a host across cases and need to isolate one send
    /// from the last.
    /// </remarks>
    public void Clear() => _sent.Clear();

    /// <inheritdoc />
    public Task<EmailDeliveryResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Honoured for consistency with the real providers: a test that
        // cancels should see the same behaviour whichever provider is
        // configured.
        cancellationToken.ThrowIfCancellationRequested();

        _sent.Enqueue(new CapturedEmail(message, _timeProvider.GetUtcNow()));

        while (_sent.Count > MaxRetained && _sent.TryDequeue(out _))
        {
            // Drop from the front until back within the bound. The loop
            // condition rather than a single dequeue because concurrent
            // enqueues may have pushed it several over.
        }

        // Recipient and subject only. The body is deliberately not logged, even
        // here: it contains the verification or reset link, and those links are
        // bearer credentials — anyone who can read the log can take over the
        // account. That is true of a developer's terminal scrollback and far
        // more true of an aggregated log store, which is where this ends up if
        // anyone ever selects this provider outside Development.
        //
        // A developer who needs to open the link has two better routes than a
        // log line: read Sent, or point Email:Provider at Smtp and run a local
        // catcher such as Mailpit, which renders both parts properly and is
        // what the SMTP adapter's loopback allowance exists for.
        _logger.LogInformation(
            "Email NOT SENT (capture provider active). To: {Recipient}. Subject: {Subject}",
            message.To.Address,
            message.Subject);

        return Task.FromResult(EmailDeliveryResult.Success());
    }
}
