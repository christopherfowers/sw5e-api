using System.Diagnostics.CodeAnalysis;

namespace Sw5e.Domain.Content;

/// <summary>
/// The closed set of content types the API serves.
/// </summary>
/// <remarks>
/// This is deliberately a hard-coded list rather than a directory listing. The
/// <c>{type}</c> route value ends up in a path join in the filesystem-backed
/// store and in a table selection in the database-backed one, so it must be
/// resolved to a member of this set <em>before</em> either store sees it.
/// Resolution returns one of these instances, so no caller ever forwards the
/// caller's own string onward.
/// </remarks>
public static class ContentTypeRegistry
{
    /// <summary>
    /// Every content type, in the order the site's navigation shows them.
    /// </summary>
    public static IReadOnlyList<ContentTypeDefinition> All { get; } =
    [
        new("source", "Source", "Sources", "sources"),
        new("species", "Species", "Species", "species"),
        new("background", "Background", "Backgrounds", "backgrounds"),
        new("archetype", "Archetype", "Archetypes", "archetypes"),
        new("feature", "Feature", "Features", "features"),
        new("feat", "Feat", "Feats", "feats"),
        new("power", "Power", "Powers", "powers"),
        new("equipment", "Equipment", "Equipment", "equipment"),
        new("monster", "Monster", "Monsters", "monsters"),
    ];

    private static readonly Dictionary<string, ContentTypeDefinition> ByName =
        BuildLookup();

    /// <summary>
    /// Resolves a caller-supplied type name to a registry entry. Accepts either
    /// the canonical key ("species") or the route segment the site uses
    /// ("backgrounds"), matched case-insensitively. Returns false for anything
    /// else, including empty input, so an unknown or hostile value never
    /// reaches a store.
    /// </summary>
    public static bool TryResolve(
        string? name,
        [NotNullWhen(true)] out ContentTypeDefinition? definition)
    {
        definition = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return ByName.TryGetValue(name, out definition);
    }

    private static Dictionary<string, ContentTypeDefinition> BuildLookup()
    {
        var lookup = new Dictionary<string, ContentTypeDefinition>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var definition in All)
        {
            lookup[definition.Key] = definition;
            lookup[definition.RouteSegment] = definition;
        }

        return lookup;
    }
}

/// <summary>
/// A registry entry: the static half of <see cref="ContentTypeDescriptor"/>,
/// without the item count that only a store can supply.
/// </summary>
public sealed record ContentTypeDefinition(
    string Key,
    string DisplayName,
    string PluralName,
    string RouteSegment);
