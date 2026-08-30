using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sw5e.Email.Configuration;

/// <summary>
/// States which provider is active, once, at startup.
/// </summary>
/// <remarks>
/// <para>
/// Configuration that fails is loud already — registration throws. This covers
/// the other half: configuration that succeeds, but not the way anyone
/// intended. The startup log is where "why did nobody get their reset email"
/// gets answered in one line, and it is written before the first request rather
/// than being inferred from the absence of later ones.
/// </para>
/// <para>
/// <see cref="EmailProvider.Capture"/> outside Development gets a warning
/// rather than a refusal. It is legitimate — a staging environment exercising
/// account flows without emailing anyone is a reasonable thing to want — but it
/// is also exactly what a misconfigured production environment looks like, so
/// it does not get to be quiet.
/// </para>
/// <para>
/// Nothing secret is logged: the provider, the sending identity and the retry
/// budget only. The token and the SMTP password are never printed, not even
/// redacted, because a redacted secret in a log still tells a reader its
/// length.
/// </para>
/// </remarks>
internal sealed class EmailStartupAnnouncer : IHostedService
{
    private readonly EmailProvider _provider;
    private readonly EmailOptions _options;
    private readonly ILogger _logger;

    public EmailStartupAnnouncer(
        EmailProvider provider,
        EmailOptions options,
        ILoggerFactory loggerFactory)
    {
        _provider = provider;
        _options = options;
        _logger = loggerFactory.CreateLogger("Sw5e.Email");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_provider is EmailProvider.Capture)
        {
            _logger.LogWarning(
                "Email is using the {Provider} provider: messages will be written to this log " +
                "and NOT delivered to anyone. This is the intended behaviour in Development; " +
                "anywhere else, set Email__Provider.",
                _provider);
        }
        else
        {
            _logger.LogInformation(
                "Email is using the {Provider} provider, sending as {From} with up to " +
                "{MaxAttempts} attempts per message.",
                _provider,
                _options.FromName is null
                    ? _options.FromAddress
                    : $"{_options.FromName} <{_options.FromAddress}>",
                _options.Retry.MaxAttempts);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
