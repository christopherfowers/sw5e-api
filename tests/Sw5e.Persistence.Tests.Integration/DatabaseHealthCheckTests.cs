using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;
using Sw5e.Infrastructure.Persistence;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// What the database health check reports, in each of the three states a
/// deployment can be in.
/// </summary>
public sealed class DatabaseHealthCheckTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "health_tests";

    protected override bool Migrate => false;

    protected override bool ImportContent => false;

    [DockerFact]
    public async Task ReportsHealthyOnceTheSchemaIsUpToDate()
    {
        await Database.MigrateAsync();

        var result = await CheckAsync(Database);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    /// <summary>
    /// A reachable database whose schema is behind the build is degraded, not
    /// healthy.
    /// </summary>
    /// <remarks>
    /// This is the whole reason the check does more than <c>SELECT 1</c>.
    /// Nothing migrates on startup here, so a deploy that ships new code and
    /// forgets the migrator produces exactly this state — and without the
    /// check, the first sign of it is a 500 from whichever endpoint touches the
    /// new column, long after the deploy was declared successful. A plain
    /// connectivity probe reports this state as perfectly healthy.
    /// </remarks>
    [DockerFact]
    public async Task ReportsDegradedWhenMigrationsHaveNotBeenApplied()
    {
        var result = await CheckAsync(Database);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldNotBeNull();
        result.Description!.ShouldContain("behind this build");
        result.Description.ShouldContain("migrator");
    }

    [DockerFact]
    public async Task ReportsUnhealthyWhenTheDatabaseCannotBeReached()
    {
        // A port nothing is listening on, on a host that resolves. The
        // credentials are deliberately distinctive so the assertion below about
        // what is not disclosed has something to look for.
        var unreachable = new NpgsqlConnectionStringBuilder(Database.ConnectionString)
        {
            Port = 65_432,
            Password = "a-secret-that-must-not-be-echoed",
            Timeout = 2,
        };

        await using var broken = new ContentDatabase(unreachable.ConnectionString, ContentFixture.Path);

        var result = await CheckAsync(broken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);

        // The readiness endpoint returns the description to anything that can
        // reach the API, so a connection failure must not become a disclosure
        // of where the database lives or what the credential is.
        result.Description.ShouldNotBeNull();
        result.Description!.ShouldNotContain("a-secret-that-must-not-be-echoed");
        result.Description.ShouldNotContain("65432");
        result.Description.ShouldNotContain(unreachable.Host!);
    }

    private static async Task<HealthCheckResult> CheckAsync(ContentDatabase database)
    {
        var check = (Sw5eDatabaseHealthCheck)ActivatorUtilities.CreateInstance(
            database.Services, typeof(Sw5eDatabaseHealthCheck));

        return await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration(
                    Sw5eDatabaseHealthCheck.Name,
                    _ => check,
                    HealthStatus.Unhealthy,
                    tags: null),
            });
    }
}
