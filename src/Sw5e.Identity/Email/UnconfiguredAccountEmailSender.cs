using Microsoft.Extensions.Logging;

namespace Sw5e.Identity.Email;

/// <summary>
/// The stand-in registered when no real <see cref="IAccountEmailSender"/> has
/// been supplied. It throws.
/// </summary>
/// <remarks>
/// <para>
/// The obvious alternative — a no-op that logs and returns — is a security bug.
/// Every account flow here is gated on the user receiving a link: if delivery
/// silently does nothing, registration still creates an account, the endpoint
/// still answers "check your email", and a deployment can sit for weeks in a
/// state where nobody can finish signing up and nothing in the logs says why.
/// Worse, the recovery flow would report success while sending nothing, which
/// is indistinguishable from an attacker's request being quietly dropped.
/// </para>
/// <para>
/// So this fails closed and loudly, at the first attempt to send, with a
/// message that names the missing registration.
/// </para>
/// </remarks>
internal sealed class UnconfiguredAccountEmailSender(ILogger<UnconfiguredAccountEmailSender> logger)
    : IAccountEmailSender
{
    public Task SendEmailVerificationAsync(
        AccountEmailRecipient recipient, string verificationUrl, CancellationToken cancellationToken = default) =>
        Fail(nameof(SendEmailVerificationAsync));

    public Task SendPasskeyRecoveryAsync(
        AccountEmailRecipient recipient, string recoveryUrl, CancellationToken cancellationToken = default) =>
        Fail(nameof(SendPasskeyRecoveryAsync));

    public Task SendSecurityNoticeAsync(
        AccountEmailRecipient recipient, string summary, CancellationToken cancellationToken = default) =>
        Fail(nameof(SendSecurityNoticeAsync));

    public Task SendSignInCodeAsync(
        AccountEmailRecipient recipient,
        string code,
        TimeSpan validFor,
        CancellationToken cancellationToken = default) =>
        Fail(nameof(SendSignInCodeAsync));

    public Task SendUnknownAddressSignInNoticeAsync(
        string emailAddress, CancellationToken cancellationToken = default) =>
        Fail(nameof(SendUnknownAddressSignInNoticeAsync));

    private Task Fail(string operation)
    {
        // Logged as well as thrown: the exception reaches the caller as a 500
        // with no detail, by design, so the log is the only place the operator
        // finds out which call failed.
        logger.LogError(
            "No IAccountEmailSender is registered, so {Operation} cannot be delivered. " +
            "Register a mail provider before serving account traffic.", operation);

        throw new InvalidOperationException(
            "No IAccountEmailSender implementation is registered. Account email " +
            "cannot be delivered, so registration, verification and recovery are " +
            "all inoperable. Register an implementation during startup.");
    }
}
