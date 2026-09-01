using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Sw5e.Identity.EmailSignIn;

namespace Sw5e.Identity;

/// <summary>
/// The identity store: accounts, roles, passkeys, and the tokens that back
/// email verification and two-factor enrolment.
/// </summary>
/// <remarks>
/// <para>
/// This is a separate <see cref="DbContext"/> from the content store, in a
/// separate PostgreSQL schema, and that separation is deliberate rather than
/// tidy-mindedness. Content and identity have nothing in common: content is
/// public, bulk-imported, frequently rebuilt and safe to restore from a
/// snapshot; identity is none of those things. Keeping them apart means a
/// content migration can never rewrite an account table, a content restore can
/// never roll credentials backwards, and a role granted to the content
/// importer never reaches a single row of account data.
/// </para>
/// <para>
/// The schema name is fixed rather than configurable. A configurable schema
/// means two deployments of the same code can disagree about where accounts
/// live, and the failure mode of that disagreement — an empty identity schema,
/// therefore no administrators, therefore an open door for whoever registers
/// first — is far worse than the inconvenience of a constant.
/// </para>
/// </remarks>
public sealed class Sw5eIdentityDbContext(DbContextOptions<Sw5eIdentityDbContext> options)
    : IdentityDbContext<Sw5eUser, Sw5eRole, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>The PostgreSQL schema every identity table lives in.</summary>
    public const string Schema = "identity";

    /// <summary>
    /// The data protection key ring.
    /// </summary>
    /// <remarks>
    /// These keys encrypt and sign the session cookie, the two-factor cookie,
    /// the passkey challenge cookies and every emailed token, so they belong in
    /// the same database as the accounts they protect: backed up together,
    /// restored together, and shared by every replica without a mounted volume.
    /// See AddSw5eIdentity for what goes wrong when they are not persisted.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>
    /// Email sign-in codes, issued and spent.
    /// </summary>
    /// <remarks>
    /// A table of live credentials, which is why it is the only table here that
    /// deletes its own rows: see <see cref="EmailSignInCodeService"/>, which
    /// prunes an address's history every time it issues for that address.
    /// </remarks>
    public DbSet<EmailSignInCode> EmailSignInCodes => Set<EmailSignInCode>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(Schema);

        builder.Entity<Sw5eUser>(user =>
        {
            // Long enough for anything a person will type, short enough that a
            // display name cannot be used as free storage. The value is echoed
            // back to other users, so its length is attacker-controlled input.
            user.Property(u => u.DisplayName).HasMaxLength(64).IsRequired();

            // The framework declares EmailIndex over NormalizedEmail but leaves
            // it non-unique. Redeclaring it by the same name makes it unique in
            // place rather than adding a second index over the same column.
            //
            // IdentityOptions.User.RequireUniqueEmail already makes the
            // UserManager check first, but a check is not a constraint: two
            // registrations arriving at once both find the address free and
            // both insert. The unique index is what actually decides, in the
            // one place where the race cannot be lost.
            user.HasIndex(u => u.NormalizedEmail)
                .HasDatabaseName("EmailIndex")
                .IsUnique();
        });

        builder.Entity<EmailSignInCode>(code =>
        {
            code.ToTable("EmailSignInCodes");
            code.HasKey(c => c.Id);

            // 254 characters, matching the longest address the registration
            // endpoint will accept. Bounded rather than unbounded because this
            // column is written from unauthenticated input on every request to
            // the code endpoint.
            code.Property(c => c.NormalizedEmail).HasMaxLength(254).IsRequired();
            code.Property(c => c.CodeSalt).IsRequired();
            code.Property(c => c.CodeHash).IsRequired();

            // Every query in the flow — counting an address's recent codes,
            // finding the live one, pruning the spent ones — filters on the
            // address and orders by time. One composite index serves all three,
            // and without it the endpoint an unauthenticated caller can reach
            // most cheaply is the one that scans this table.
            code.HasIndex(c => new { c.NormalizedEmail, c.CreatedAt })
                .HasDatabaseName("IX_EmailSignInCodes_Address");

            // No foreign key to the user, and that is deliberate rather than an
            // oversight. Rows are written for addresses that have no account at
            // all — that is what keeps the request path taking the same time
            // either way — so the column has to be free to hold an identifier
            // that matches nothing. It is also why deleting an account cannot
            // cascade here: the codes expire on their own within minutes, and a
            // cascade would be a second, slower way to say so.
            code.Property(c => c.UserId);
        });
    }
}
