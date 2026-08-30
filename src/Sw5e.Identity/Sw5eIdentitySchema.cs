using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Sw5e.Identity;

/// <summary>
/// The one thing the runtime and the migration tooling must agree on.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET Core Identity's EF Core context builds a different model per schema
/// version, and passkeys exist only from version 3. Version selection is read
/// out of <see cref="IdentityOptions"/> through the context's application
/// service provider — and when there is no service provider, which is exactly
/// the situation <c>dotnet ef</c> is in, it silently falls back to version 1.
/// </para>
/// <para>
/// The failure that produces is quiet and expensive: the migration scaffolds a
/// version 1 schema with no <c>AspNetUserPasskeys</c> table at all, the
/// application starts against it perfectly happily, and the first attempt to
/// register a passkey fails at the database. Nothing in the build, the tests or
/// the startup log points at the cause.
/// </para>
/// <para>
/// So the version lives here as a constant, both the runtime registration and
/// the design-time factory read it, and neither is free to drift.
/// </para>
/// </remarks>
internal static class Sw5eIdentitySchema
{
    /// <summary>
    /// The identity schema version this application targets. Version 3 is the
    /// first that includes passkey storage.
    /// </summary>
    public static readonly Version Version = IdentitySchemaVersions.Version3;

    /// <summary>
    /// Builds the minimal service provider the identity context needs in order
    /// to resolve the schema version at design time.
    /// </summary>
    public static IServiceProvider CreateDesignTimeServiceProvider() =>
        new ServiceCollection()
            .Configure<IdentityOptions>(identity => identity.Stores.SchemaVersion = Version)
            .BuildServiceProvider();
}
