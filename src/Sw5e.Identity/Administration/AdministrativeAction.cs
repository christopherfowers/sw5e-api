namespace Sw5e.Identity.Administration;

/// <summary>
/// One thing an administrator did to somebody else's account.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Until now the only record that an account's
/// privileges had been changed was a log line, and a log line is not a record:
/// it is retained for as long as whatever aggregates it decides to retain it,
/// it cannot be queried by the people who need it, and it is written by the
/// same process that performed the action. The set of accounts that can grant
/// the administrator role is the set of accounts that can do anything at all
/// here, and how that set came to have the members it has needs to be a
/// question with an answer.
/// </para>
/// <para>
/// <b>Modelled on the flag queue's actor-and-timestamp pattern rather than on
/// something new.</b> <c>ContentFlagRow</c> already records who moved a report
/// and when, as a bare <see cref="Guid"/> with no foreign key and a UTC
/// timestamp, and resolves display names at read time. This does the same, for
/// the same reasons, with one deliberate difference: the actor's and the
/// subject's display names are <em>copied onto the row</em>. A flag can afford
/// to render "a removed account" because the report is still legible without a
/// name. An audit record of an account's deletion cannot: the whole point of it
/// is to survive the deletion, and resolving the subject's name at read time
/// would guarantee that the one action most worth recording is the one that
/// reads as an identifier and nothing else.
/// </para>
/// <para>
/// <b>What is copied is the display name and never the address.</b> This
/// platform's rule everywhere else is that an email address is disclosed to the
/// account that owns it and to nobody else, and an audit table is not an
/// exception it gets to make for itself — it is a table that outlives the
/// account, which makes it exactly the wrong place to keep an address after the
/// person asked to be deleted.
/// </para>
/// <para>
/// <b>Append-only, and the database says so.</b> The migration installs a
/// trigger that raises on any <c>UPDATE</c> or <c>DELETE</c> here, the same way
/// the content revision table is protected. The value of this table is entirely
/// the confidence that nobody edited it, and an administrator is precisely the
/// person with both the access and the motive.
/// </para>
/// </remarks>
public sealed class AdministrativeAction
{
    /// <summary>
    /// Surrogate key, a version 7 <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// Sortable by creation time, so the index that serves the newest-first
    /// listing is also the primary key's, and unguessable, so an identifier
    /// appearing in a URL does not publish how many administrative actions the
    /// platform has ever taken.
    /// </remarks>
    public Guid Id { get; set; }

    /// <summary>What was done, as its published wire spelling.</summary>
    /// <remarks>
    /// A string in the database rather than an enum ordinal, for the reason the
    /// moderation schema gives: an ordinal is unreadable in <c>psql</c> and
    /// changes meaning the moment a member is inserted into the middle of the
    /// enum. See <see cref="AdministrativeActionKind"/>.
    /// </remarks>
    public required string Action { get; set; }

    /// <summary>The administrator who did it.</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>Their display name at the time.</summary>
    public required string ActorDisplayName { get; set; }

    /// <summary>The account it was done to.</summary>
    public Guid SubjectUserId { get; set; }

    /// <summary>Its display name at the time.</summary>
    public required string SubjectDisplayName { get; set; }

    /// <summary>
    /// The assignable roles the subject held beforehand, comma separated, or
    /// null for an action that was not about roles.
    /// </summary>
    /// <remarks>
    /// Stored as text rather than as a relation. Three role names is not a
    /// dimension worth a join table, and the value being read here is a
    /// historical statement — what the roles <em>were</em> — which a foreign
    /// key to a live role table could not express anyway.
    /// </remarks>
    public string? RolesBefore { get; set; }

    /// <summary>The assignable roles the subject held afterwards.</summary>
    public string? RolesAfter { get; set; }

    /// <summary>
    /// What the administrator said about why, or null.
    /// </summary>
    /// <remarks>
    /// Bounded and stored verbatim, exactly like a reviewer's note on a flag:
    /// never sanitised on the way in, escaped at every point of output. It is
    /// written by an administrator, which makes it less hostile than a
    /// reporter's prose and not trustworthy — an administrator's session can be
    /// stolen, and this text is rendered to other administrators.
    /// </remarks>
    public string? Reason { get; set; }

    /// <summary>When it happened, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The administrative actions this platform records.
/// </summary>
/// <remarks>
/// The list is closed and it is short, and both are on purpose. An audit log
/// that records everything is one nobody reads; this records the four things
/// that change what an account is allowed to be, and every one of them is
/// something a person decided rather than something the system did on its own.
/// Lockouts are absent for that reason — a lockout is the framework counting
/// failures, and putting it here would bury the four decisions under a stream
/// of automation.
/// </remarks>
public enum AdministrativeActionKind
{
    /// <summary>The subject's assignable roles were declared afresh.</summary>
    RolesChanged,

    /// <summary>The subject was suspended.</summary>
    AccountSuspended,

    /// <summary>The subject's suspension was lifted.</summary>
    AccountReinstated,

    /// <summary>The subject's account was deleted.</summary>
    AccountDeleted,
}

/// <summary>
/// The wire spellings of <see cref="AdministrativeActionKind"/>, which are also
/// what is written to the database.
/// </summary>
/// <remarks>
/// A single table mapping members to strings, in one place, because the two
/// consumers that must never disagree are the column and the JSON. Deriving
/// either from <c>Enum.ToString()</c> would make a C# rename a silent data
/// migration in one direction and a silent contract break in the other.
/// </remarks>
public static class AdministrativeActionWire
{
    /// <summary>Longest wire name, and therefore the column's length.</summary>
    public const int MaxNameLength = 32;

    private static readonly (AdministrativeActionKind Kind, string Name)[] Names =
    [
        (AdministrativeActionKind.RolesChanged, "roles-changed"),
        (AdministrativeActionKind.AccountSuspended, "account-suspended"),
        (AdministrativeActionKind.AccountReinstated, "account-reinstated"),
        (AdministrativeActionKind.AccountDeleted, "account-deleted"),
    ];

    /// <summary>Every action name, in the order the enum declares them.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Names.Select(entry => entry.Name)];

    /// <summary>The wire spelling of one action.</summary>
    public static string NameOf(AdministrativeActionKind kind) =>
        Names.FirstOrDefault(entry => entry.Kind == kind).Name
        ?? throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "No wire name is defined for that administrative action.");

    /// <summary>Reads a wire spelling back, ordinally.</summary>
    public static bool TryParse(string? name, out AdministrativeActionKind kind)
    {
        foreach (var entry in Names)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                kind = entry.Kind;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
