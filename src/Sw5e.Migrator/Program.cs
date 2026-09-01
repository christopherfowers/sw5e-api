using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sw5e.Infrastructure.Persistence;
using Sw5e.Infrastructure.Persistence.Moderation;
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
// start whenever it was given something to do — and it rejects a bare flag such
// as "--check" for the same reason, which is why the export's own switches are
// taken out here rather than looked up in configuration.
var parsed = MigratorArguments.Parse(args);

var builder = Host.CreateApplicationBuilder(parsed.Settings);

builder.Services.AddSw5ePersistence(builder.Configuration);
builder.Services.AddSw5eContentImporter();

// The exporter, when this build has the schemas beside it — which the published
// image always does, because it bakes them in from the same submodule commit
// the validator comes from.
//
// Registered conditionally rather than unconditionally so that `migrate` and
// `import`, which need no schemas at all, keep working when run from a
// directory that has none. `export` says so plainly when it is missing, which
// puts the failure on the command that actually needed it.
var schemaPath = builder.Configuration["Content:SchemaPath"] ?? "schemas";

// Resolved against the directory the assemblies are in rather than the working
// directory: in the image that is /app, where the schemas are, and a job's
// working directory is not something the deployment sets.
var schemaRoot = Path.IsPathRooted(schemaPath)
    ? schemaPath
    : Path.Combine(AppContext.BaseDirectory, schemaPath);

if (Directory.Exists(schemaRoot))
{
    builder.Services.AddSw5eContentExporter(schemaRoot);
}

// The moderation schema, which is applied by the same job for the same reason
// the content one is: a web process that can change a schema holds rights at
// runtime it has no business holding, and N replicas racing to apply one
// migration is not a plan.
//
// It resolves its own connection string through the same method the API uses,
// so the schema is created in the database the endpoints will write to. Two
// implementations of that precedence is how a deployment ends up migrating one
// database and writing to another, which fails at the first report rather than
// at deploy time.
builder.Services.AddSw5eModeration(builder.Configuration);

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

if (parsed.Error is not null)
{
    logger.LogError(
        "{Error} Usage: migrate | import | all | export --output <path> [--type <content-type>] " +
        "[--key <slug>] [--check] [--no-prune]",
        parsed.Error);

    return MigratorCommands.UnknownCommand;
}

return await MigratorCommands.RunAsync(
    host.Services, parsed.Command, logger, cancellation.Token, parsed.Export);
