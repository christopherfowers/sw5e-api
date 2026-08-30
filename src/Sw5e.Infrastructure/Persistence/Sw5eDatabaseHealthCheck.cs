using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Infrastructure.Persistence;

/// <summary>
/// Reports whether the database is reachable and whether its schema is the one
/// this build expects.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it checks for pending migrations.</b> This application deliberately
/// does not migrate on startup, so a deploy that ships new code and skips the
/// migrator leaves a schema the code does not match. Without this check the
/// first symptom is a 500 from whichever endpoint touches the new column,
/// minutes or hours later, on a request from a real user. With it, the deploy's
/// own readiness probe says so immediately, in a message that names the
/// problem.
/// </para>
/// <para>
/// It reports degraded rather than unhealthy for that case on purpose. An
/// application whose schema is one migration behind is usually still serving
/// every request correctly — the new column is not read by any code path the
/// old rows reach — and marking it unhealthy would take a working deployment
/// out of rotation over a problem that is fixed by running a job. Unreachable
/// is a different matter: nothing works, and unhealthy is the honest answer.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It does not count rows or touch
/// content. A liveness or readiness probe runs every few seconds forever, and a
/// probe that does real work is a self-inflicted load generator that gets
/// heavier exactly when the database is already struggling.
/// </para>
/// </remarks>
public sealed class Sw5eDatabaseHealthCheck(
    IDbContextFactory<Sw5eContentDbContext> contextFactory,
    IOptions<Sw5eDatabaseOptions> options) : IHealthCheck
{
    /// <summary>Name this check is registered under.</summary>
    public const string Name = "database";

    /// <summary>
    /// Tag identifying checks that gate readiness rather than liveness.
    /// </summary>
    /// <remarks>
    /// Liveness must not depend on the database. A liveness probe that fails
    /// during a database outage makes the orchestrator restart every API
    /// container, which does nothing for the database and removes the capacity
    /// that would have served the cached and static responses that still work.
    /// </remarks>
    public const string ReadyTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            if (!await database.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("The SW5e database is not reachable.");
            }

            if (!options.Value.ReportPendingMigrations)
            {
                return HealthCheckResult.Healthy("The SW5e database is reachable.");
            }

            var pending = await database.Database.GetPendingMigrationsAsync(cancellationToken);
            var count = pending.Count();

            return count == 0
                ? HealthCheckResult.Healthy("The SW5e database is reachable and up to date.")
                : HealthCheckResult.Degraded(
                    $"The SW5e content schema is behind this build by {count} migration(s). " +
                    "Run the migrator.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message is not included in the description. A connection
            // failure from Npgsql names the host, the port, the database and
            // sometimes the user, and the health endpoint is reachable by
            // anything that can reach the API.
            return HealthCheckResult.Unhealthy(
                "The SW5e database could not be queried.",
                exception);
        }
    }
}
