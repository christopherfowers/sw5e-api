using System.Text;
using System.Text.Json;

namespace Sw5e.Infrastructure.Content;

/// <summary>
/// Turns a validated content document into the small set of fields a list row
/// or a search result row needs.
/// </summary>
/// <remarks>
/// The per-type field lists below are the filesystem store's stand-in for the
/// projected columns a database table would carry. Keeping them in one table
/// means the database implementation can be built to expose the same columns
/// rather than having to reverse-engineer them out of endpoint code.
/// </remarks>
internal static class ContentProjection
{
    /// <summary>Longest summary line kept, in characters.</summary>
    private const int MaxSummaryLength = 200;

    /// <summary>
    /// Cap on the harvested search text per item. Monster stat blocks are the
    /// largest documents in the corpus and still fit comfortably; the cap only
    /// exists so a malformed or hostile file cannot inflate the index.
    /// </summary>
    private const int MaxSearchTextLength = 16_000;

    private sealed record TypeProjection(
        string NameField,
        string[] SummaryFields,
        string[] FacetFields);

    private static readonly Dictionary<string, TypeProjection> Projections =
        new(StringComparer.Ordinal)
        {
            ["source"] = new(
                "title",
                ["publisher", "licenseNote"],
                ["abbreviation", "publisher", "publishedAt", "isOfficial"]),
            ["species"] = new(
                "name",
                ["lore"],
                ["size", "homeworld", "nativeLanguage"]),
            ["background"] = new(
                "name",
                ["lore"],
                ["feature.name", "skillProficiencies"]),
            ["archetype"] = new(
                "name",
                ["description"],
                ["className", "casterType", "classCasterType"]),
            ["feature"] = new(
                "name",
                ["description"],
                ["grantedBy", "grantedByName", "level"]),
            ["feat"] = new(
                "name",
                ["description"],
                ["prerequisite"]),
            ["power"] = new(
                "name",
                ["description"],
                ["powerType", "level", "forceAlignment", "range", "duration", "concentration"]),

            // The combat options. What a reader filters each of these lists on
            // is different, which is the whole reason they are six types: a
            // maneuver list is scanned for its list and its die cost, a weapon
            // focus list for the weapon group it applies to, and the two
            // Formfighting entries are the only styles anyone filters for a
            // prerequisite at all.
            ["maneuver"] = new(
                "name",
                ["description"],
                ["maneuverType", "superiorityDice", "prerequisite", "improves"]),
            ["fighting-style"] = new(
                "name",
                ["description"],
                ["prerequisite"]),
            ["fighting-mastery"] = new(
                "name",
                ["description"],
                ["prerequisite"]),

            // A form has no top-level prose: its rules text is split into the
            // effect that fires as the form is adopted and the one that holds
            // while it is worn. The first of those is what a one-line summary
            // should show, which is why the path runs through the array.
            ["lightsaber-form"] = new(
                "name",
                ["effects.description"],
                ["prerequisite"]),

            ["weapon-focus"] = new(
                "name",
                ["description"],
                ["weaponGroup"]),
            ["weapon-supremacy"] = new(
                "name",
                ["description"],
                ["weaponGroup"]),
            ["equipment"] = new(
                "name",
                ["description"],
                ["category", "costInCredits", "weight", "weaponClassification", "armorClassification"]),
            ["monster"] = new(
                "name",
                ["flavorText", "sectionText"],
                ["size", "types", "alignment", "challengeRating", "experiencePoints"]),

            // Starship types. The facets are the columns a reader scans a
            // shipyard list by, which is a different question per type: what a
            // modification costs in slots (its grade) and what upgrades it
            // continues (its type), what mounting a weapon needs, what rank a
            // venture is gated behind. `savingThrows` earns a facet on base
            // sizes because it is the only proficiency a hull is born with.
            ["starship-base-size"] = new(
                "name",
                ["lore"],
                ["savingThrows", "modifications.baseModificationSlots"]),
            ["starship-deployment"] = new(
                "name",
                ["role"],
                ["role"]),
            ["starship-equipment"] = new(
                "name",
                ["description"],
                [
                    "category",
                    "costInCredits",
                    "weapon.mounting",
                    "weapon.weaponSize",
                    "hyperdriveClass",
                ]),
            ["starship-modification"] = new(
                "name",
                ["description"],
                ["modificationType", "grade"]),
            ["starship-venture"] = new(
                "name",
                ["description"],
                []),
            ["starship-rule"] = new(
                // Chapters are titled, not named, exactly as sources are.
                "title",
                ["body"],
                ["chapterNumber"]),

            ["credit-category"] = new(
                "title",
                ["description"],
                ["order"]),
            // A credit's contribution is its summary line because it is the
            // part worth reading: "for the epic cover and SW5e logo" says what
            // somebody did, whereas their category alone says only that they
            // were involved. Category is a facet so credits can be filtered
            // into their groups without the caller knowing the key format.
            ["credit"] = new(
                "name",
                ["contribution"],
                ["categoryKey", "order"]),
            // Asset credits are keyed by the picture, so the facets are what
            // identify the picture and how it may be shown. Artist is a facet
            // rather than only a display field so that "everything by this
            // artist" is answerable once the citations start being filled in.
            ["asset-credit"] = new(
                "assetKey",
                ["provenance", "basisNote"],
                ["assetGroup", "assetKey", "status", "artist", "workTitle", "basis"]),
        };

    /// <summary>
    /// Name of the field holding the display name for a type. Sources call it
    /// "title"; every other type calls it "name".
    /// </summary>
    public static string NameField(string typeKey) =>
        Projections.TryGetValue(typeKey, out var projection) ? projection.NameField : "name";

    /// <summary>The type-specific display fields for a row, absent fields omitted.</summary>
    /// <remarks>
    /// Ordered by field name rather than by the order of the list above.
    /// Nothing about a row's rendering depends on the order — the site reads
    /// these by name — but search does: when a query matches more than one
    /// display field, the first match is the one reported as the explanation,
    /// so the iteration order decides which field a user is told about. A
    /// database-backed store cannot reproduce "the order this array happens to
    /// be written in" without carrying it in the row; it can reproduce a sort.
    /// Making the order a sort in both stores is what keeps the explanation the
    /// same whichever store answered.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Facets(string typeKey, JsonElement body)
    {
        var facets = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (!Projections.TryGetValue(typeKey, out var projection))
        {
            return facets;
        }

        foreach (var path in projection.FacetFields)
        {
            if (TryResolve(body, path, out var element) && TryRender(element, out var rendered))
            {
                facets[path] = rendered;
            }
        }

        return facets;
    }

    /// <summary>The one-line description under a row, or null when the item has no prose.</summary>
    public static string? Summary(string typeKey, JsonElement body)
    {
        if (!Projections.TryGetValue(typeKey, out var projection))
        {
            return null;
        }

        foreach (var path in projection.SummaryFields)
        {
            if (!TryResolve(body, path, out var element) ||
                element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = PlainText.Flatten(element.GetString());

            if (text.Length > 0)
            {
                return PlainText.Truncate(text, MaxSummaryLength);
            }
        }

        return null;
    }

    /// <summary>
    /// Every piece of prose in the document, flattened into one plain-text blob
    /// for substring search.
    /// </summary>
    /// <remarks>
    /// Harvested generically rather than from a per-type list of prose fields:
    /// a field added to a schema becomes searchable without a change here, and
    /// a field missed by a hand-written list is the kind of gap nobody notices
    /// until a user reports that search cannot find something. Image URLs are
    /// excluded because a hit inside one is noise no reader can interpret.
    /// </remarks>
    public static string SearchText(JsonElement body)
    {
        var builder = new StringBuilder();
        Harvest(body, propertyName: null, builder);
        return builder.ToString();
    }

    private static void Harvest(JsonElement element, string? propertyName, StringBuilder builder)
    {
        if (builder.Length >= MaxSearchTextLength)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Harvest(property.Value, property.Name, builder);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Harvest(item, propertyName, builder);
                }

                break;

            case JsonValueKind.String:
                if (string.Equals(propertyName, "imageUrls", StringComparison.Ordinal))
                {
                    return;
                }

                var text = PlainText.Flatten(element.GetString());

                if (text.Length > 0)
                {
                    builder.Append(text).Append('\n');
                }

                break;

            default:
                // Numbers, booleans and nulls carry no prose worth matching: a
                // search for "5" should not return every power of level 5.
                break;
        }
    }

    /// <summary>
    /// Walks a dotted path into a document.
    /// </summary>
    /// <remarks>
    /// A segment applied to an array resolves against that array's first
    /// element. This exists for the types whose prose is a list rather than a
    /// field — a lightsaber form's <c>effects</c> — and first is the right
    /// element rather than an arbitrary one: these lists are stored in printed
    /// order, so the first entry is the one the books lead with and the one a
    /// single-line summary should show. An empty array resolves to nothing,
    /// which is the same outcome as a missing field and is handled the same
    /// way by both callers.
    /// </remarks>
    private static bool TryResolve(JsonElement body, string path, out JsonElement result)
    {
        result = body;

        foreach (var segment in path.Split('.'))
        {
            if (result.ValueKind == JsonValueKind.Array)
            {
                var first = result.EnumerateArray();

                if (!first.MoveNext())
                {
                    result = default;
                    return false;
                }

                result = first.Current;
            }

            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty(segment, out result))
            {
                result = default;
                return false;
            }
        }

        return true;
    }

    private static bool TryRender(JsonElement element, out string rendered)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                rendered = PlainText.Truncate(
                    PlainText.Flatten(element.GetString()),
                    MaxSummaryLength);
                return rendered.Length > 0;

            case JsonValueKind.Number:
                rendered = element.GetRawText();
                return true;

            case JsonValueKind.True:
                rendered = "true";
                return true;

            case JsonValueKind.False:
                rendered = "false";
                return true;

            case JsonValueKind.Array:
                var parts = new List<string>();

                foreach (var item in element.EnumerateArray())
                {
                    if (TryRender(item, out var part))
                    {
                        parts.Add(part);
                    }
                }

                rendered = string.Join(", ", parts);
                return rendered.Length > 0;

            default:
                rendered = string.Empty;
                return false;
        }
    }
}
