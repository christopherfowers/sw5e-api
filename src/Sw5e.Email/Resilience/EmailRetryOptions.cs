namespace Sw5e.Email.Resilience;

/// <summary>
/// The retry budget, bound from <c>Email:Retry</c>.
/// </summary>
/// <remarks>
/// The numbers are chosen against a hard constraint: these sends happen while
/// somebody is watching a spinner on a registration or password-reset form. The
/// worst case is <see cref="MaxAttempts"/> multiplied by the per-attempt
/// provider timeout, plus the backoff between them, and that total has to stay
/// under what a user and a reverse proxy will both tolerate. With the defaults
/// here and MailerSend's ten-second timeout that is roughly forty seconds of
/// requests and under eight of waiting — bad, but bounded, and only reached
/// when the provider is genuinely broken.
/// </remarks>
public sealed class EmailRetryOptions
{
    /// <summary>
    /// Total attempts, the first one included. Four means one send and up to
    /// three retries; one disables retrying without needing a separate flag.
    /// </summary>
    /// <remarks>
    /// Four rather than more because the failures worth retrying are brief —
    /// a rate-limit window, a rolling deploy on the provider's side, a dropped
    /// connection. Anything still failing after four attempts is an outage, and
    /// an outage is not fixed by a fifth attempt made while a user waits.
    /// </remarks>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// The base of the exponential backoff: the nominal wait before the second
    /// attempt, doubling thereafter.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The ceiling on any single wait, and the limit on how long a provider's
    /// own <c>Retry-After</c> may be before the send is abandoned instead.
    /// </summary>
    /// <remarks>
    /// The second half of that is the important half. MailerSend answers a
    /// rate-limited request with <c>retry-after: 59</c>, and sleeping fifty-nine
    /// seconds inside an HTTP request is not resilience — it exhausts the
    /// thread pool and trips every timeout upstream. When the provider asks for
    /// longer than this, the send fails as transient and the decision about
    /// what to do next belongs to the caller.
    /// </remarks>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);
}
