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
        new("class", "Class", "Classes", "classes"),
        new("class-improvement", "Class improvement", "Class improvements", "class-improvements"),
        new("archetype", "Archetype", "Archetypes", "archetypes"),
        new("feature", "Feature", "Features", "features"),
        new("feat", "Feat", "Feats", "feats"),
        new("power", "Power", "Powers", "powers"),

        // The combat options. Six types rather than one, because a character
        // chooses from six separate lists and nothing lets an entry on one
        // stand in for an entry on another.
        //
        // `maneuver` is the entry to be careful with. Its canonical directory,
        // its schema and its key are singular like every other type here, but
        // the site has been serving /maneuvers since before any of this content
        // existed — the type is in the navigation and renders an empty index.
        // The route segment therefore has to be the plural the site already
        // publishes, or the day the content lands the API answers on an address
        // nothing links to and the page stays empty for a different reason than
        // before. TryResolve accepts both spellings, so this costs nothing.
        new("maneuver", "Maneuver", "Maneuvers", "maneuvers"),
        new("fighting-style", "Fighting Style", "Fighting Styles", "fighting-styles"),
        new("fighting-mastery", "Fighting Mastery", "Fighting Masteries", "fighting-masteries"),
        new("lightsaber-form", "Lightsaber Form", "Lightsaber Forms", "lightsaber-forms"),
        new("weapon-focus", "Weapon Focus", "Weapon Focuses", "weapon-focuses"),
        new("weapon-supremacy", "Weapon Supremacy", "Weapon Supremacies", "weapon-supremacies"),

        new("equipment", "Equipment", "Equipment", "equipment"),

        // Grouped with equipment rather than appended, because that is where a
        // reader looks for them: an enhanced item is gear, and the two property
        // glossaries are the definitions an equipment or enhanced-item row
        // refers to. Weapon and armour properties are two types rather than one
        // because four names — interlocking, silent, strength and versatile —
        // are published in both glossaries with different rules, so a merged
        // type could answer a lookup with the wrong rule.
        new("enhanced-item", "Enhanced item", "Enhanced items", "enhanced-items"),
        new("weapon-property", "Weapon property", "Weapon properties", "weapon-properties"),
        new("armor-property", "Armor property", "Armor properties", "armor-properties"),

        new("monster", "Monster", "Monsters", "monsters"),

        // Starship play. These sit after the character types because that is
        // the order a table reaches them: a group builds characters first and
        // acquires a ship later. `starship-equipment` and `starship-rule` are
        // the two whose plural is not the singular plus an "s", which is why
        // every entry spells its plural out rather than deriving one.
        new("starship-base-size", "Starship Base Size", "Starship Base Sizes", "starship-base-sizes"),
        new("starship-deployment", "Starship Deployment", "Starship Deployments", "starship-deployments"),
        new("starship-equipment", "Starship Equipment", "Starship Equipment", "starship-equipment"),
        new("starship-modification", "Starship Modification", "Starship Modifications", "starship-modifications"),
        new("starship-venture", "Starship Venture", "Starship Ventures", "starship-ventures"),
        new("starship-rule", "Starship Rule", "Starship Rules", "starship-rules"),

        // Last, because neither is a catalogue of things a character can have.
        // A rule is a passage to read and a reference table is a lookup to jump
        // to, so they belong at the end of the navigation rather than among the
        // types a player browses while building a character.
        new("rule", "Rule", "Rules", "rules"),
        new("reference-table", "Reference table", "Reference tables", "reference-tables"),

        // Attribution. These are not game content and the site does not put
        // them in its navigation, but they are edited, reviewed and served by
        // exactly the same machinery, which is the whole reason they are
        // content types rather than a hand-maintained page: a credit is a
        // record somebody has to be able to correct.
        new("credit-category", "Credit category", "Credit categories", "credit-categories"),
        new("credit", "Credit", "Credits", "credits"),
        new("asset-credit", "Asset credit", "Asset credits", "asset-credits"),
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
