using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sw5e.Identity;

/// <summary>
/// Lets <c>dotnet ef migrations</c> construct the context without starting the
/// application.
/// </summary>
/// <remarks>
/// The connection string here is a syntactically valid placeholder and nothing
/// more. Migration scaffolding needs a provider so it knows PostgreSQL's types
/// and quoting rules; it never opens the connection. Pointing this at a real
/// database — or at a real credential — would put a connection string in the
/// repository for no benefit whatsoever, so it does not.
/// </remarks>
public sealed class Sw5eIdentityDbContextFactory : IDesignTimeDbContextFactory<Sw5eIdentityDbContext>
{
    public Sw5eIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<Sw5eIdentityDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=sw5e_design_time;Username=design_time",
                npgsql => npgsql.MigrationsHistoryTable(
                    "__EFMigrationsHistory", Sw5eIdentityDbContext.Schema))
            // Without this the scaffolded model is Identity's version 1 schema,
            // which has no passkey table. See Sw5eIdentitySchema.
            .UseApplicationServiceProvider(Sw5eIdentitySchema.CreateDesignTimeServiceProvider())
            .Options;

        return new Sw5eIdentityDbContext(options);
    }
}
