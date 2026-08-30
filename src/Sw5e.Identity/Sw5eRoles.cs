namespace Sw5e.Identity;

/// <summary>
/// Every role the platform recognises, and the authorization policies built
/// from them.
/// </summary>
/// <remarks>
/// <para>
/// The list is closed on purpose. Roles are a privilege ladder, and a ladder
/// whose rungs can be added at runtime is one database write away from being a
/// privilege escalation. Adding a role means editing this file, which means it
/// goes through review.
/// </para>
/// <para>
/// Authorize against the policies rather than against role names. A policy is
/// one place to change when "who may contribute" stops meaning exactly one
/// role, whereas <c>[Authorize(Roles = "Contributor")]</c> scattered across
/// endpoints is a set of places somebody will miss.
/// </para>
/// </remarks>
public static class Sw5eRoles
{
    /// <summary>
    /// The default for every account. Grants nothing beyond what an anonymous
    /// visitor already has; it exists so that "signed in" is a state the API
    /// can reason about, not so that signing in unlocks the catalogue.
    /// </summary>
    public const string Community = "Community";

    /// <summary>
    /// A small, hand-picked set of trusted people who may upload base game
    /// rules and content. Granted only by an administrator, never on request
    /// and never automatically.
    /// </summary>
    public const string Contributor = "Contributor";

    /// <summary>
    /// Full administrative control, including granting and revoking every other
    /// role. Expected to be a handful of accounts at most.
    /// </summary>
    public const string Administrator = "Administrator";

    /// <summary>The roles that exist, in ascending order of privilege.</summary>
    public static readonly IReadOnlyList<string> All = [Community, Contributor, Administrator];

    /// <summary>
    /// The roles an administrator is allowed to grant or revoke through the
    /// API. <see cref="Community"/> is excluded: it is the floor every account
    /// stands on, and removing it would produce an account in a state no other
    /// code expects.
    /// </summary>
    public static readonly IReadOnlyList<string> Assignable = [Contributor, Administrator];
}

/// <summary>Authorization policy names. See <see cref="Sw5eRoles"/>.</summary>
public static class Sw5ePolicies
{
    /// <summary>Requires a signed-in account, whatever its roles.</summary>
    public const string SignedIn = "sw5e:signed-in";

    /// <summary>
    /// Requires the ability to upload base game content. Administrators are
    /// included so that the person who hands out the role is never locked out
    /// of the thing the role unlocks.
    /// </summary>
    public const string Contribute = "sw5e:contribute";

    /// <summary>Requires full administrative control.</summary>
    public const string Administer = "sw5e:administer";
}
