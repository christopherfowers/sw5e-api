using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sw5e.Identity;

/// <summary>
/// Brings the identity database up to date and makes sure the role table
/// contains exactly the roles the code knows about.
/// </summary>
/// <remarks>
/// Registered only when <see cref="Sw5eIdentityOptions.InitializeDatabaseAtStartup"/>
/// is set. See that property for why the default is off.
/// </remarks>
internal sealed class Sw5eIdentityInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<Sw5eIdentityOptions> options,
    ILogger<Sw5eIdentityInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await InitializeAsync(scope.ServiceProvider, options.Value, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Migrates the schema, seeds the roles and applies the bootstrap
    /// administrator promotion. Safe to run repeatedly and safe to run
    /// concurrently: every step is idempotent, and the migrator takes
    /// PostgreSQL's own advisory lock so two processes cannot apply the same
    /// migration twice.
    /// </summary>
    public static async Task InitializeAsync(
        IServiceProvider services,
        Sw5eIdentityOptions options,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var context = services.GetRequiredService<Sw5eIdentityDbContext>();
        await context.Database.MigrateAsync(cancellationToken);

        await SeedRolesAsync(services, logger, cancellationToken);
        await PromoteBootstrapAdministratorAsync(services, options, logger);
    }

    private static async Task SeedRolesAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var roles = services.GetRequiredService<RoleManager<Sw5eRole>>();

        foreach (var name in Sw5eRoles.All)
        {
            if (await roles.RoleExistsAsync(name))
            {
                continue;
            }

            var result = await roles.CreateAsync(new Sw5eRole(name));

            if (!result.Succeeded)
            {
                // A role that failed to seed is not a cosmetic problem: every
                // authorization policy on the site is written against these
                // names, so carrying on would serve traffic whose permission
                // checks can only ever fail closed for legitimate users, or —
                // far worse — pass for nobody and leave content unmanageable.
                throw new InvalidOperationException(
                    $"Could not seed the '{name}' role: " +
                    string.Join("; ", result.Errors.Select(error => error.Description)));
            }

            logger.LogInformation("Seeded the {Role} role.", name);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task PromoteBootstrapAdministratorAsync(
        IServiceProvider services,
        Sw5eIdentityOptions options,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapAdministratorEmail))
        {
            return;
        }

        var users = services.GetRequiredService<UserManager<Sw5eUser>>();
        var user = await users.FindByEmailAsync(options.BootstrapAdministratorEmail);

        if (user is null)
        {
            // Not an error, and deliberately not one. The expected sequence is
            // that the setting is configured before the named person has
            // registered; the promotion then happens on the next restart after
            // they do. Creating the account here instead would mean the
            // platform manufacturing an administrator nobody proved control of.
            logger.LogInformation(
                "No account exists yet for the configured bootstrap administrator. " +
                "It will be promoted once that address has registered.");
            return;
        }

        if (await users.IsInRoleAsync(user, Sw5eRoles.Administrator))
        {
            return;
        }

        var result = await users.AddToRoleAsync(user, Sw5eRoles.Administrator);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not promote the bootstrap administrator: " +
                string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        // Logged loudly and without the address. A role change is the single
        // most privilege-relevant event the platform can record, and the audit
        // trail for it starts here.
        logger.LogWarning(
            "Granted the {Role} role to the configured bootstrap administrator (account {UserId}).",
            Sw5eRoles.Administrator,
            user.Id);
    }
}
