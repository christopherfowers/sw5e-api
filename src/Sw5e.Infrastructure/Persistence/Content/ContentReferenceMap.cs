using System.Globalization;
using System.Text.Json;

namespace Sw5e.Infrastructure.Persistence.Content;

/// <summary>One cross-reference found in a document, before it is resolved.</summary>
/// <param name="Relation">What kind of link this is.</param>
/// <param name="JsonPath">Where in the document it was found. Unique per item and relation.</param>
/// <param name="TargetType">Type of content it points at. Need not be a registered type.</param>
/// <param name="TargetKind">Whether the identifier is a slug or a display name.</param>
/// <param name="TargetIdentifier">The slug or name the document gave, trimmed.</param>
/// <param name="Ordinal">Position among the links of the same relation on the same item.</param>
internal sealed record ExtractedReference(
    string Relation,
    string JsonPath,
    string TargetType,
    ContentReferenceTargetKind TargetKind,
    string TargetIdentifier,
    int Ordinal);

/// <summary>
/// The rules that turn one SW5e content document into the edges of the content
/// graph.
/// </summary>
/// <remarks>
/// <para>
/// This is the part of the store that is specific to SW5e rather than to
/// "documents in PostgreSQL". Every rule below corresponds to a field the
/// published JSON Schemas describe as pointing at another piece of content, and
/// each is written out explicitly rather than inferred, because inference over
/// this corpus does not work: there is no naming convention that separates a
/// reference from prose. <c>sourceKey</c> ends in "Key" and is one;
/// <c>grantedByName</c> ends in "Name" and is also one; <c>homeworld</c> and
/// <c>alignment</c> are strings that look exactly like names and are neither.
/// </para>
/// <para>
/// <b>Why almost every rule produces a name reference.</b> The corpus was
/// transcribed from print, where the only identifier a cross-reference can use
/// is the printed name. Exactly one field points at another item by slug —
/// <c>sourceKey</c>. Everything else names its target: a feature says which
/// archetype grants it by writing the archetype's name, a background lists its
/// feat options by name, a power names the power it requires. Resolving those
/// is a join on <c>name</c>, and it can be ambiguous, so the importer resolves
/// a name only when exactly one candidate matches.
/// </para>
/// <para>
/// <b>What is deliberately not extracted, and why.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <c>equipment.properties[]</c>. The schema says the leading word of each
/// clause names a weapon or armour property, but the data disagrees with the
/// schema: "power cell (range 105/420)" names a two-word property, so splitting
/// on the first token yields "power", which is not the name of anything. A rule
/// that produces a wrong edge is worse than no rule, because a wrong edge
/// resolves and is therefore invisible. This waits for the property content
/// types to be authored.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>monster.behaviors[].descriptionWithLinks</c>. This is the one field in
/// the whole corpus designed to carry explicit cross-references, as Markdown
/// links. Not one document populates it — there is not a single Markdown link
/// anywhere in the content — so a parser for it would be untested code guarding
/// against nothing. It becomes a rule here the day the field carries data.
/// </description>
/// </item>
/// <item>
/// <description>
/// <c>background.startingEquipment</c>. The schema asks for item names that
/// match equipment entries; the prose says "a set of common clothes" where the
/// equipment is named "Clothes, common". Nothing matches verbatim, so linking
/// these would need fuzzy matching, and a fuzzy edge in a table whose whole
/// value is that its edges are trustworthy is a bad trade.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal static class ContentReferenceMap
{
    /// <summary>
    /// Longest identifier accepted. Comfortably past the longest real name and
    /// short enough that a malformed document cannot inflate the graph.
    /// </summary>
    private const int MaxIdentifierLength = 512;

    /// <summary>
    /// Relation names. A closed set, so a query can ask for one kind of edge
    /// without knowing which types produce it.
    /// </summary>
    internal static class Relations
    {
        /// <summary>The publication an item was printed in.</summary>
        public const string Source = "source";

        /// <summary>The class, archetype or species that grants a feature.</summary>
        public const string GrantedBy = "grantedBy";

        /// <summary>The class an archetype belongs to.</summary>
        public const string Class = "class";

        /// <summary>A feat a background offers as an option.</summary>
        public const string FeatOption = "featOption";

        /// <summary>A feat named in another feat's prerequisite.</summary>
        public const string PrerequisiteFeat = "prerequisiteFeat";

        /// <summary>The power a power requires.</summary>
        public const string PrerequisitePower = "prerequisitePower";

        /// <summary>A species a half-human variant draws traits from.</summary>
        public const string HalfHumanSpecies = "halfHumanSpecies";

        /// <summary>A maneuver named in another maneuver's prerequisite.</summary>
        public const string PrerequisiteManeuver = "prerequisiteManeuver";

        /// <summary>The maneuver a tiered maneuver upgrades.</summary>
        public const string ImprovesManeuver = "improvesManeuver";

        /// <summary>A starship modification another modification is built on.</summary>
        public const string PrerequisiteStarshipModification = "prerequisiteStarshipModification";

        /// <summary>A piece of starship equipment a modification requires fitted.</summary>
        public const string PrerequisiteStarshipEquipment = "prerequisiteStarshipEquipment";

        /// <summary>A starship venture another venture is built on.</summary>
        public const string PrerequisiteStarshipVenture = "prerequisiteStarshipVenture";

        /// <summary>The deployment a venture requires a rank in.</summary>
        public const string PrerequisiteStarshipDeployment = "prerequisiteStarshipDeployment";

        /// <summary>A launcher that fires a piece of starship ammunition.</summary>
        public const string AmmunitionLauncher = "ammunitionLauncher";
    }

    /// <summary>
    /// Prerequisite fields the starship types carry, paired with the content
    /// type each points at and the relation it produces.
    /// </summary>
    /// <remarks>
    /// The starship prerequisite lists are the one place in this corpus where a
    /// reference is already resolved in the document: the import parsed each
    /// printed clause and, where it could name what the clause meant, wrote the
    /// target into a field of its own beside the wording. So unlike a feat's
    /// prerequisite, which has to be picked out of prose here, these are read
    /// straight off the entry — and an entry whose field is absent is one the
    /// import deliberately declined to resolve, not one this map should guess at.
    /// </remarks>
    private static readonly (string Field, string TargetType, string Relation)[]
        StarshipPrerequisiteFields =
        [
            ("modificationName", "starship-modification", Relations.PrerequisiteStarshipModification),
            ("equipmentName", "starship-equipment", Relations.PrerequisiteStarshipEquipment),
            ("ventureName", "starship-venture", Relations.PrerequisiteStarshipVenture),
            ("deploymentName", "starship-deployment", Relations.PrerequisiteStarshipDeployment),
        ];

    /// <summary>
    /// The types a feature can name as its grantor. Closed, because
    /// <c>grantedBy</c> is an enum in the schema and an unrecognised value is a
    /// corrupt document rather than a new kind of link.
    /// </summary>
    private static readonly string[] GrantorTypes = ["class", "archetype", "species"];

    /// <summary>Extracts every cross-reference one document declares.</summary>
    /// <param name="typeKey">The item's content type key.</param>
    /// <param name="body">The document.</param>
    internal static IReadOnlyList<ExtractedReference> Extract(string typeKey, JsonElement body)
    {
        var references = new List<ExtractedReference>();

        // Universal: every type but source records the book it came from, and
        // source has no provenance of its own because it is the provenance.
        if (TryReadString(body, "sourceKey", out var sourceKey))
        {
            Add(references, Relations.Source, "$.sourceKey", "source",
                ContentReferenceTargetKind.Key, sourceKey);
        }

        switch (typeKey)
        {
            case "feature":
                ExtractGrantor(body, references);
                break;

            // Both name their class the same way and for the same reason: an
            // archetype is a branch of exactly one class, and an improvement
            // describes exactly one class. Neither is reachable from anywhere
            // else, so this edge is the only route to them.
            case "archetype":
            case "class-improvement":
                if (TryReadString(body, "className", out var className))
                {
                    Add(references, Relations.Class, "$.className", "class",
                        ContentReferenceTargetKind.Name, className);
                }

                break;

            case "background":
                ExtractFeatOptions(body, references);
                break;

            case "feat":
                ExtractFeatPrerequisites(body, references);
                break;

            case "power":
                // A power's prerequisite is the bare name of another power, not
                // a sentence, so it is taken whole rather than parsed.
                if (TryReadString(body, "prerequisite", out var requiredPower))
                {
                    Add(references, Relations.PrerequisitePower, "$.prerequisite", "power",
                        ContentReferenceTargetKind.Name, requiredPower);
                }

                break;

            case "species":
                ExtractHalfHumanSpecies(body, references);
                break;

            case "maneuver":
                // Two edges, and they are not the same edge written twice. The
                // prerequisite is the gate and names the tier immediately
                // below; `improves` names the base maneuver the whole chain
                // hangs off. For a third tier those are different documents —
                // Administer Aid (Greater) requires Administer Aid (Improved)
                // and improves Administer Aid — so collapsing them would lose
                // either the chain or the gate.
                ExtractManeuverPrerequisites(body, references);

                if (TryReadString(body, "improves", out var improvedManeuver))
                {
                    Add(references, Relations.ImprovesManeuver, "$.improves", "maneuver",
                        ContentReferenceTargetKind.Name, improvedManeuver);
                }

                break;

            case "starship-modification":
            case "starship-venture":
                ExtractStarshipPrerequisites(body, references);
                break;

            case "starship-equipment":
                ExtractAmmunitionLaunchers(body, references);
                break;
        }

        return references;
    }

    /// <summary>
    /// Reads the resolved targets out of a starship prerequisite list.
    /// </summary>
    /// <remarks>
    /// One entry can produce at most one edge, and most produce none: a clause
    /// that requires a ship size, a weapon mounting or twelve Constitution has
    /// nothing in this database to point at. The array index is part of the
    /// path because a modification can require two different modifications, and
    /// a chain such as the plating upgrades requires an armour and a
    /// modification from the same list.
    /// </remarks>
    private static void ExtractStarshipPrerequisites(
        JsonElement body,
        List<ExtractedReference> references)
    {
        if (!body.TryGetProperty("prerequisites", out var prerequisites) ||
            prerequisites.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;

        foreach (var entry in prerequisites.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            foreach (var (field, targetType, relation) in StarshipPrerequisiteFields)
            {
                if (TryReadString(entry, field, out var target))
                {
                    Add(references, relation, Path("$.prerequisites", index, field),
                        targetType, ContentReferenceTargetKind.Name, target, index);
                }
            }

            index++;
        }
    }

    /// <summary>
    /// Links a piece of ammunition to the launchers that fire it.
    /// </summary>
    /// <remarks>
    /// The edge runs from the ammunition rather than from the launcher because
    /// that is the direction the book prints: ammunition is listed under its
    /// launcher's heading, and a launcher's own row says nothing about what it
    /// takes. Both directions are queryable once the edge exists.
    /// </remarks>
    private static void ExtractAmmunitionLaunchers(
        JsonElement body,
        List<ExtractedReference> references)
    {
        if (!body.TryGetProperty("firedBy", out var launchers) ||
            launchers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;

        foreach (var launcher in launchers.EnumerateArray())
        {
            if (launcher.ValueKind == JsonValueKind.String &&
                launcher.GetString() is { Length: > 0 } name)
            {
                Add(references, Relations.AmmunitionLauncher,
                    Path("$.firedBy", index, null), "starship-equipment",
                    ContentReferenceTargetKind.Name, name, index);
            }

            index++;
        }
    }

    private static void ExtractGrantor(JsonElement body, List<ExtractedReference> references)
    {
        if (!TryReadString(body, "grantedBy", out var grantedBy) ||
            !TryReadString(body, "grantedByName", out var grantedByName))
        {
            return;
        }

        if (!GrantorTypes.Contains(grantedBy, StringComparer.Ordinal))
        {
            return;
        }

        // The target type comes from the document, which is why it is checked
        // against a closed list first: it becomes a column value that queries
        // filter on, and letting a document choose it would let a document
        // invent a kind of content.
        Add(references, Relations.GrantedBy, "$.grantedByName", grantedBy,
            ContentReferenceTargetKind.Name, grantedByName);
    }

    private static void ExtractFeatOptions(JsonElement body, List<ExtractedReference> references)
    {
        if (!body.TryGetProperty("featOptions", out var options) ||
            options.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;

        foreach (var option in options.EnumerateArray())
        {
            // The path carries the array index, so it stays unique even when
            // two rows of the roll table offer the same feat.
            if (option.ValueKind == JsonValueKind.Object &&
                TryReadString(option, "name", out var featName))
            {
                Add(references, Relations.FeatOption,
                    Path("$.featOptions", index, "name"), "feat",
                    ContentReferenceTargetKind.Name, featName, index);
            }

            index++;
        }
    }

    /// <summary>
    /// Pulls the maneuver names out of a maneuver's prerequisite sentence.
    /// </summary>
    /// <remarks>
    /// The same shape of prose as a feat prerequisite, and the same
    /// conservative rule: a maneuver prerequisite mixes conditions of different
    /// kinds — "Proficiency in Medicine", "The ability to cast force powers",
    /// "Administer Aid maneuver" — and only the clauses ending in the word
    /// "maneuver" name one. The rest are mechanical conditions with no target
    /// to point at. A missing edge shows up in the unresolved report; a wrong
    /// one does not.
    /// </remarks>
    private static void ExtractManeuverPrerequisites(JsonElement body, List<ExtractedReference> references)
    {
        if (!TryReadString(body, "prerequisite", out var prerequisite))
        {
            return;
        }

        var ordinal = 0;

        foreach (var clause in prerequisite.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string suffix = " maneuver";

            if (!clause.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Add(references, Relations.PrerequisiteManeuver,
                Path("$.prerequisite", ordinal, null), "maneuver",
                ContentReferenceTargetKind.Name,
                clause[..^suffix.Length],
                ordinal);

            ordinal++;
        }
    }

    private static void ExtractHalfHumanSpecies(JsonElement body, List<ExtractedReference> references)
    {
        if (!body.TryGetProperty("halfHumanTraits", out var traits) ||
            traits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;

        foreach (var trait in traits.EnumerateArray())
        {
            if (trait.ValueKind == JsonValueKind.Object &&
                TryReadString(trait, "speciesName", out var speciesName))
            {
                Add(references, Relations.HalfHumanSpecies,
                    Path("$.halfHumanTraits", index, "speciesName"), "species",
                    ContentReferenceTargetKind.Name, speciesName, index);
            }

            index++;
        }
    }

    /// <summary>
    /// Pulls the feat names out of a feat's prerequisite sentence.
    /// </summary>
    /// <remarks>
    /// A feat prerequisite is prose that mixes conditions of different kinds:
    /// "4th level, Durable feat" is a level requirement and a feat requirement
    /// in one string. Only the clauses that end in the word "feat" name a feat;
    /// the rest are mechanical conditions with no target to point at, and are
    /// left for the rule engine that will eventually parse them properly. This
    /// is a narrow, conservative rule on purpose — it extracts the clauses that
    /// unambiguously name a feat and ignores everything it is not sure about,
    /// because a missing edge is visible in a report and a wrong one is not.
    /// </remarks>
    private static void ExtractFeatPrerequisites(JsonElement body, List<ExtractedReference> references)
    {
        if (!TryReadString(body, "prerequisite", out var prerequisite))
        {
            return;
        }

        var ordinal = 0;

        foreach (var clause in prerequisite.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string suffix = " feat";

            if (!clause.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The clause index rather than the field path alone: one string can
            // name several feats, and every edge from one item needs its own
            // path for the uniqueness constraint to mean anything.
            Add(references, Relations.PrerequisiteFeat,
                Path("$.prerequisite", ordinal, null), "feat",
                ContentReferenceTargetKind.Name,
                clause[..^suffix.Length],
                ordinal);

            ordinal++;
        }
    }

    private static string Path(string root, int index, string? property)
    {
        var indexed = string.Create(
            CultureInfo.InvariantCulture,
            $"{root}[{index}]");

        return property is null ? indexed : $"{indexed}.{property}";
    }

    private static void Add(
        List<ExtractedReference> references,
        string relation,
        string jsonPath,
        string targetType,
        ContentReferenceTargetKind kind,
        string identifier,
        int ordinal = 0)
    {
        var trimmed = identifier.Trim();

        if (trimmed.Length == 0 || trimmed.Length > MaxIdentifierLength)
        {
            return;
        }

        references.Add(new ExtractedReference(relation, jsonPath, targetType, kind, trimmed, ordinal));
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var found) &&
            found.ValueKind == JsonValueKind.String)
        {
            var text = found.GetString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
