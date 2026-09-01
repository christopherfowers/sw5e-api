using Sw5e.Email;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Remembers whether account mail is actually reaching the provider.
/// </summary>
/// <remarks>
/// <para>
/// It exists because of what the account endpoints are not allowed to do with a
/// delivery failure. <c>register</c> and <c>email/code</c> answer identically
/// whether or not the address has an account — same status, same body, same
/// work — and a failed send cannot be allowed to disturb that, which rules out
/// both an error response and any per-address hint in the successful one. The
/// caller therefore learns nothing, and something else has to learn everything;
/// this is that something else.
/// </para>
/// <para>
/// The state deliberately has no per-address dimension. It records that mail is
/// or is not getting out, and nothing about who it was for, so there is no
/// arrangement of requests that turns a reading of it into an answer about a
/// particular address. Anything that surfaces it — a health check, an operator
/// dashboard, a banner on the site — is reading one global fact.
/// </para>
/// <para>
/// The provider's reply is deliberately <em>not</em> held here. That string is
/// operator-facing text which may quote the envelope and so name a recipient;
/// it goes to the application log, which has an audience that is already
/// trusted with it, and never into state that something anonymous can read
/// back.
/// </para>
/// <para>Registered as a singleton and safe for concurrent use.</para>
/// </remarks>
internal sealed class AccountEmailDeliveryMonitor(TimeProvider clock)
{
    /// <summary>
    /// How long a failure keeps counting after the last one.
    /// </summary>
    /// <remarks>
    /// A window rather than a latch, because the failure that matters is the
    /// one happening now. A relay that refused a message an hour ago and has
    /// delivered nothing since is still broken; one that failed at midnight in
    /// a deployment nobody has used since is not something to page anyone
    /// about, and a status that never clears itself is a status people learn to
    /// ignore. Successful delivery clears it immediately regardless.
    /// </remarks>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();

    private DateTimeOffset? _lastFailureAt;
    private EmailFailureKind _lastFailureKind;
    private int _consecutiveFailures;

    /// <summary>What is currently known about delivery.</summary>
    public AccountEmailDeliveryStatus Current
    {
        get
        {
            lock (_gate)
            {
                if (_lastFailureAt is not { } failedAt ||
                    clock.GetUtcNow() - failedAt > FailureWindow)
                {
                    return AccountEmailDeliveryStatus.Working;
                }

                return new AccountEmailDeliveryStatus(
                    false, _lastFailureKind, failedAt, _consecutiveFailures);
            }
        }
    }

    /// <summary>The provider accepted a message.</summary>
    public void RecordSuccess()
    {
        lock (_gate)
        {
            _lastFailureAt = null;
            _consecutiveFailures = 0;
        }
    }

    /// <summary>The provider refused one, after any retrying was exhausted.</summary>
    public void RecordFailure(EmailFailureKind kind)
    {
        lock (_gate)
        {
            _lastFailureAt = clock.GetUtcNow();
            _lastFailureKind = kind;

            // Saturates rather than wrapping. A count that went negative during
            // a long outage would read as healthy on whatever consumes it, and
            // the exact figure past a handful is not information anybody acts
            // on differently.
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, int.MaxValue - 1);
        }
    }
}

/// <summary>
/// Whether account mail is getting out, with no reference to any address.
/// </summary>
/// <param name="Delivering">
/// True when nothing has failed inside
/// <see cref="AccountEmailDeliveryMonitor.FailureWindow"/>. This is the whole
/// of what may be shown to an unauthenticated reader.
/// </param>
/// <param name="LastFailureKind">
/// Whether the last failure was worth repeating. Null while delivering.
/// </param>
/// <param name="LastFailureAt">When that failure happened. Null while delivering.</param>
/// <param name="ConsecutiveFailures">
/// How many sends have failed since the last successful one, which is what
/// separates one refused message from a relay that is down.
/// </param>
internal readonly record struct AccountEmailDeliveryStatus(
    bool Delivering,
    EmailFailureKind? LastFailureKind,
    DateTimeOffset? LastFailureAt,
    int ConsecutiveFailures)
{
    /// <summary>Nothing has failed recently.</summary>
    public static AccountEmailDeliveryStatus Working { get; } = new(true, null, null, 0);
}
