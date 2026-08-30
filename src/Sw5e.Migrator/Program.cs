using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sw5e.Infrastructure.Persistence;
using Sw5e.Migrator;

// The deploy-time database job: apply migrations, then load the canonical
// content.
//
// This is a separate executable rather than a few lines in the API's startup
// because migrating on startup is the wrong shape for anything that matters.
// Every replica runs startup, so N replicas race to apply the same migration; a
// rolling deploy runs old and new code against whatever schema the first
// container reached; a failed migration takes the application down instead of
// failing a job; and the schema ends up changed by whoever happened to restart a
// container rather than by a step someone chose to run. As a job it runs once,
// in a known order, with an exit code — and a deploy that forgets to run it is
// reported by the API's health check rather than discovered by a user.

// Only settings-shaped arguments are handed to the configuration builder. The
// command-line provider rejects a bare word such as "migrate" with a format
// error, so passing args straight through would make the migrator refuse to
// start whenever it was given something to do.
var settings = args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
var command = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? "all";

var builder = Host.CreateApplicationBuilder(settings);

builder.Services.AddSw5ePersistence(builder.Configuration);
builder.Services.AddSw5eContentImporter();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Sw5e.Migrator");

// Honoured so a container stopped mid-run tears its transaction down rather
// than being killed with one open.
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await MigratorCommands.RunAsync(host.Services, command, logger, cancellation.Token);
