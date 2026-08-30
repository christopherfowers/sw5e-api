using System.ComponentModel.DataAnnotations;

namespace Sw5e.Infrastructure.Persistence;

/// <summary>
/// The knobs on the shared PostgreSQL connection. Everything here is a
/// deployment concern; nothing here changes what the application means.
/// </summary>
/// <remarks>
/// <para>
/// The connection string itself is deliberately not a property on this type. It
/// lives at <c>ConnectionStrings:Sw5e</c>, which is where every .NET operator,
/// tool and hosting platform already looks for one, and which binds from the
/// environment as <c>ConnectionStrings__Sw5e</c> without any extra mapping. It
/// also carries the password, and keeping it out of an options object that
/// might one day be logged or surfaced on a diagnostics page is worth the
/// slight asymmetry.
/// </para>
/// <para>
/// <c>ConnectionStrings:Sw5e</c> is the platform-wide connection string and the
/// one this content store uses. Identity reads its own —
/// <c>Identity:ConnectionString</c>, then <c>ConnectionStrings:Sw5eIdentity</c>,
/// falling back to this one — so a small deployment can run everything through
/// a single role while a larger one gives account data a role, or a database,
/// with no rights over content at all. Nothing here assumes the two resolve to
/// the same server, and nothing here should: content and identity are never
/// written in one transaction.
/// </para>
/// </remarks>
public sealed class Sw5eDatabaseOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Sw5e:Database";

    /// <summary>Name of the connection string every Sw5e database context uses.</summary>
    public const string ConnectionStringName = "Sw5e";

    /// <summary>
    /// How long a single command may run before it is cancelled.
    /// </summary>
    /// <remarks>
    /// Kept low on purpose. Every query this application issues against the
    /// content schema is a single-page read over a table of a few hundred rows;
    /// one that has not answered in fifteen seconds is not slow, it is stuck,
    /// and holding the request open makes the outage worse rather than better.
    /// The migrator overrides this, because applying a migration legitimately
    /// takes longer than serving a page.
    /// </remarks>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// How many times a command that failed with a transient error is retried.
    /// </summary>
    /// <remarks>
    /// Transient here means what Npgsql classifies as transient: a dropped
    /// connection, a serialisation failure, an admin shutdown during a
    /// failover. A constraint violation or a syntax error is not retried, so
    /// this cannot turn a deterministic bug into a slow deterministic bug.
    /// </remarks>
    [Range(0, 10)]
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Ceiling on the backoff between retries.</summary>
    [Range(1, 120)]
    public int MaxRetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Whether the health check reports a schema behind its migrations as
    /// degraded.
    /// </summary>
    /// <remarks>
    /// On by default because this application does not migrate on startup: a
    /// deploy that ships new code and forgets the migrator leaves a schema the
    /// code does not match, and without this the first symptom is a 500 on
    /// whichever endpoint touches the new column. Turn it off only where the
    /// extra round trip per probe is genuinely unaffordable.
    /// </remarks>
    public bool ReportPendingMigrations { get; set; } = true;
}
