using System.Security.Cryptography;
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
    /// <summary>
    /// Bumped whenever a document's stored row is derived differently, even
    /// though the document itself has not changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row's version is a hash of the document, and the importer skips any
    /// item whose version it already holds. That is right for content edits and
    /// wrong for everything else: when the projection changes, every document
    /// in the corpus produces a different row from the same bytes, and a hash
    /// of the bytes alone cannot tell. The importer skips all of them and the
    /// change reaches only whichever documents happened to be edited in the
    /// same release.
    /// </para>
    /// <para>
    /// This is not hypothetical. Harvesting Markdown headings into their own
    /// column shipped, imported cleanly against a database that had been up for
    /// days, reported "175 updated, 7,702 unchanged" — and left the new column
    /// empty on 7,876 of 7,877 rows, because those documents had not changed.
    /// The tier that reads it was inert, and every test passed, because tests
    /// import into an empty database where every row is an insert.
    /// </para>
    /// <para>
    /// Mixing this into the version fixes it in the direction that is also
    /// correct for callers. The version is the ETag, and a projection change
    /// genuinely changes the response — a client holding the old one is holding
    /// something stale, and should be told so.
    /// </para>
    /// <para>
    /// <b>Bump this when changing anything that turns a document into a row:</b>
    /// the name, summary or facet fields below, the heading harvest, the search
    /// text, or the cap on any of them.
    /// </para>
    /// </remarks>
    internal const string Version = "3-reading-path";

    /// <summary>
    /// A stable description of which fields each type is projected from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so that changing the projection without changing
    /// <see cref="Version"/> fails a test instead of reaching a deployment.
    /// That is not a hypothetical pairing to forget: harvesting headings into
    /// their own column shipped, imported cleanly against a database that had
    /// been up for days, and left the new column empty on 7,876 of 7,877 rows
    /// because every document's version still matched. The feature was inert
    /// and every test was green.
    /// </para>
    /// <para>
    /// It fingerprints the table below and nothing else, and that limit is
    /// worth stating: it will notice a field added, removed or moved between
    /// name, summary and facets, and it will not notice a change in how a field
    /// is turned into text — the heading harvest, the summary cap, the search
    /// text. Those still need somebody to think. What it removes is the case
    /// where the change is visible in a diff as an edited list and the version
    /// two lines above it was simply not looked at.
    /// </para>
    /// </remarks>
    internal static string Fingerprint()
    {
        var builder = new StringBuilder();

        foreach (var (type, projection) in Projections.OrderBy(
                     entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(type).Append('|')
                   .Append(projection.NameField).Append('|')
                   .AppendJoin(',', projection.SummaryFields).Append('|')
                   .AppendJoin(',', projection.FacetFields).Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }


    /// <summary>Longest summary line kept, in characters.</summary>
    /// <remarks>
    /// Unchanged by the arrival of the rule type, whose summary field is a
    /// whole chapter of Markdown. <see cref="PlainText.Truncate"/> cuts it to
    /// this length on a word boundary exactly as it does a two-sentence species
    /// lore paragraph, so a chapter's row reads as its opening line. The
    /// flattening of the full body that precedes the cut is the one cost, and
    /// it is paid once per document when the index is built rather than per
    /// request.
    /// </remarks>
    private const int MaxSummaryLength = 200;

    /// <summary>
    /// Cap on the harvested search text per item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized to the corpus, not to a round number: the longest rule chapter is
    /// roughly 460,000 characters, and a rule exists precisely so a reader can
    /// find a half-remembered passage inside it. A cap below the longest
    /// legitimate document does not protect anything — it silently drops most
    /// of the rules corpus out of the index, and the symptom is a search that
    /// returns nothing and looks exactly like a passage that was never written.
    /// So the cap is set past the largest real document and left there; what it
    /// still does is bound a malformed or hostile file, which is all it was
    /// ever for.
    /// </para>
    /// <para>
    /// It is also a real bound now. <see cref="Harvest"/> used to stop
    /// <em>recursing</em> once the buffer was full but still appended whatever
    /// string it was already looking at, so one oversized field passed through
    /// whole and the cap could be overshot by the entire length of that field.
    /// The remaining budget is applied to each appended run instead.
    /// </para>
    /// </remarks>
    private const int MaxSearchTextLength = 512_000;

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
            // A class is scanned by what it plays like and what it costs to
            // multiclass into, so the primary ability, the hit die and the
            // casting ratio are the facets; the level table is not one, because
            // nothing filters on a whole table.
            ["class"] = new(
                "name",
                ["summary", "lore"],
                ["primaryAbility", "hitPoints.dieFaces", "casterType", "casterRatio"]),
            ["class-improvement"] = new(
                "name",
                ["description"],
                ["className", "improvementType", "prerequisite"]),
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

            // Enhanced items are the largest type in the corpus by an order of
            // magnitude — 1,918 documents — and nothing in them is a price, so
            // the fields below are the only way a reader narrows the list to
            // something they can read. rarity is the ladder the game places
            // loot on, itemType says what a reader has to own before the item
            // is usable, requiresAttunement is a slot a character sheet counts,
            // and subtype names the equipment or body slot the item attaches
            // to. prerequisite is projected for the same reason a feat's is: it
            // is the condition a reader checks before deciding the row is
            // relevant to them.
            ["enhanced-item"] = new(
                "name",
                ["description"],
                ["rarity", "itemType", "requiresAttunement", "subtype", "prerequisite"]),

            // No facets at all, deliberately. Either glossary entry is four
            // fields — key, name, contentSet and the rules text — and the first
            // three are already columns on every row, so anything listed here
            // could only repeat what the row carries. A facet that duplicates a
            // column is worse than none: it is a second copy of the same value
            // for a client to disagree with, and it makes a filter bar offer a
            // control that narrows nothing.
            ["weapon-property"] = new("name", ["description"], []),
            ["armor-property"] = new("name", ["description"], []),

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

            // The summary comes from the body, which for a chapter is up to
            // 460,000 characters of Markdown; MaxSummaryLength cuts it to an
            // opening line like every other type's. ruleType is the one field
            // that changes how a passage is read — a chapter is a sequence, a
            // variant is a switch a table turns on — and chapterNumber orders a
            // table of contents. Neither identifies a rule: the archive numbers
            // a preface -2 and a changelog 99, and two chapters of Wretched
            // Hives share the number 1.
            /*
              readingGroup and order are what the site builds its path from, and
              chapterNumber is deliberately still here beside them. It is true
              about the archive — where the passage fell in a printed book — and
              a facet is exactly the right place for a fact nobody navigates by
              but somebody may want to see. What changed is that it stopped
              being the only thing available to order by, which is how it ended
              up deciding that a comparison with another game came before the
              explanation of this one.
            */
            ["rule"] = new(
                "name",
                ["body"],
                ["ruleType", "chapterNumber", "readingGroup", "order"]),

            // subject is the only thing thirty otherwise unrelated tables have
            // to group by, and grouping is the whole of what a list of them can
            // offer. The body is the table itself, so the summary line is its
            // first row flattened — thin, but a caption plus a subject is what
            // a reader actually picks from.
            ["reference-table"] = new(
                "name",
                ["body"],
                ["subject"]),
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

    /// <summary>
    /// Just the headings in the document's prose, one per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept apart from <see cref="SearchText"/> so that search can tell the two
    /// apart, and it very much has to. Every match for "difficult terrain" used
    /// to land in the same tier, so the results came back ordered by nothing
    /// more meaningful than the alphabet inside whichever content type happened
    /// to have the most hits: twenty-nine class features first, and the rules
    /// chapter with a section literally titled "Difficult Terrain" fifth.
    /// </para>
    /// <para>
    /// A heading is a much stronger signal than a sentence. Somebody who types
    /// a phrase that a section is named after wants that section, not the
    /// twenty-nine places that mention it in passing.
    /// </para>
    /// <para>
    /// Headings are found with the same rule the browser's markdown parser
    /// uses — one to six hashes, whitespace, text, on a trimmed line — and that
    /// dialect has no fenced code blocks, so a line scan finds exactly the
    /// headings that will be rendered. The two live in different repositories;
    /// if the parser ever learns about fences, this has to learn with it.
    /// </para>
    /// </remarks>
    public static string HeadingText(JsonElement body)
    {
        var builder = new StringBuilder();
        HarvestHeadings(body, builder);
        return builder.ToString();
    }

    private static void HarvestHeadings(JsonElement element, StringBuilder builder)
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
                    HarvestHeadings(property.Value, builder);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    HarvestHeadings(item, builder);
                }

                break;

            case JsonValueKind.String:
                AppendHeadings(element.GetString(), builder);
                break;

            default:
                break;
        }
    }

    /// <summary>Appends every markdown heading in one string value.</summary>
    private static void AppendHeadings(string? value, StringBuilder builder)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('#', StringComparison.Ordinal))
        {
            return;
        }

        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length < 2 || trimmed[0] != '#')
            {
                continue;
            }

            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#')
            {
                hashes += 1;
            }

            // One to six hashes, and whitespace after them. "#hashtag" is not a
            // heading and neither is a row of sevens.
            if (hashes is < 1 or > 6 ||
                hashes >= trimmed.Length ||
                !char.IsWhiteSpace(trimmed[hashes]))
            {
                continue;
            }

            var text = PlainText.Flatten(trimmed[hashes..].Trim());

            if (text.Length == 0 || builder.Length >= MaxSearchTextLength)
            {
                continue;
            }

            builder.Append(text.AsSpan(0, Math.Min(text.Length, MaxSearchTextLength - builder.Length)));
            builder.Append('\n');
        }
    }

    private static void Harvest(JsonElement element, string? propertyName, StringBuilder builder)
    {
        var remaining = MaxSearchTextLength - builder.Length;

        if (remaining <= 0)
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
                    // Clamped to what is left of the budget rather than
                    // appended whole. Without this the cap bounds the number of
                    // fields harvested and not the size of the result, which is
                    // the opposite of what a cap on a hostile document has to
                    // do.
                    builder.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));

                    if (builder.Length < MaxSearchTextLength)
                    {
                        builder.Append('\n');
                    }
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
