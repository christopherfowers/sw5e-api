using System.Text.Json;
using Shouldly;
using Sw5e.Infrastructure.Persistence.Content;
using Xunit;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The edges the starship types contribute to the content graph.
/// </summary>
/// <remarks>
/// <para>
/// Extraction is tested directly rather than through an import, because that is
/// where the whole risk lives: resolution, storage and re-resolution are
/// type-agnostic and already covered by <see cref="ContentReferenceTests"/>,
/// while the rules that decide <em>which</em> fields of a starship document are
/// links are per-type and hand-written. A rule that pointed at the wrong
/// content type, or that quietly produced nothing, would still import cleanly.
/// </para>
/// <para>
/// The documents below are copies of real canonical content rather than
/// invented shapes, so a change to how the content set writes a prerequisite
/// fails here instead of silently emptying the graph.
/// </para>
/// </remarks>
public sealed class StarshipReferenceMapTests
{
    private static IReadOnlyList<ExtractedReference> Extract(string typeKey, string json)
    {
        using var document = JsonDocument.Parse(json);
        return ContentReferenceMap.Extract(typeKey, document.RootElement);
    }

    /// <summary>
    /// A plating modification requires an armour and the previous mark of
    /// itself, so one document produces two edges into two different content
    /// types from the same array.
    /// </summary>
    [Fact]
    public void Modification_LinksToBothThePartItNeedsAndTheMarkBelowIt()
    {
        var references = Extract("starship-modification",
            """
            {
              "key": "plating-reinforced-mk-iii",
              "name": "Plating, Reinforced Mk III",
              "sourceKey": "sotg",
              "modificationType": "universal",
              "grade": 3,
              "prerequisites": [
                { "kind": "equipment", "text": "Reinforced Armor", "equipmentName": "Reinforced armor" },
                { "kind": "modification", "text": "Plating, Reinforced MK II", "modificationName": "Plating, Reinforced Mk II" }
              ],
              "description": "You substantially upgrade your ship's armor plating."
            }
            """);

        references.Select(reference =>
                $"{reference.Relation} {reference.TargetType} {reference.TargetIdentifier} {reference.JsonPath}")
            .ShouldBe(
            [
                "source source sotg $.sourceKey",
                "prerequisiteStarshipEquipment starship-equipment Reinforced armor $.prerequisites[0].equipmentName",
                "prerequisiteStarshipModification starship-modification Plating, Reinforced Mk II $.prerequisites[1].modificationName",
            ]);

        references.ShouldAllBe(reference =>
            reference.Relation == "source" ||
            reference.TargetKind == ContentReferenceTargetKind.Name);
    }

    /// <summary>
    /// A clause the import declined to resolve must produce no edge at all. An
    /// extractor that fell back to the printed text would fill the unresolved
    /// report with ship sizes and Constitution requirements, and hide the real
    /// gaps behind them.
    /// </summary>
    [Fact]
    public void Modification_ProducesNoEdgeForAPrerequisiteWithNothingToPointAt()
    {
        var references = Extract("starship-modification",
            """
            {
              "key": "amphibious-systems",
              "name": "Amphibious Systems",
              "modificationType": "universal",
              "grade": 0,
              "prerequisites": [
                { "kind": "shipSize", "text": "Ship size Medium or larger",
                  "shipSizes": ["medium", "large", "huge", "gargantuan"] },
                { "kind": "weaponMounting", "text": "Primary or Secondary Weapon",
                  "mountings": ["primary", "secondary"] },
                { "kind": "other", "text": "Ship size Tiny, 12 Constitution, no Droid Brain modification" }
              ],
              "description": "This modification allows your ship to function underwater."
            }
            """);

        references.ShouldBeEmpty();
    }

    /// <summary>
    /// A venture can be gated on a rank in a named deployment, on another
    /// venture, or on something with no target — and one document can carry two
    /// of the three at once.
    /// </summary>
    [Fact]
    public void Venture_LinksToTheDeploymentAndTheVentureItIsGatedOn()
    {
        var references = Extract("starship-venture",
            """
            {
              "key": "lock-on-target",
              "name": "Lock on Target",
              "prerequisites": [
                { "kind": "casting", "text": "The ability to cast tech powers" },
                { "kind": "deploymentRank", "text": "at least 1 rank in gunner",
                  "deploymentName": "Gunner", "rank": 1 }
              ],
              "description": "Once per turn, when you hit a target with a ship attack."
            }
            """);

        var edge = references.ShouldHaveSingleItem();

        edge.Relation.ShouldBe("prerequisiteStarshipDeployment");
        edge.TargetType.ShouldBe("starship-deployment");
        edge.TargetIdentifier.ShouldBe("Gunner");
        // The ordinal is the position in the prerequisite array, not in the
        // edge list, so an entry that produced no edge does not renumber the
        // ones after it.
        edge.Ordinal.ShouldBe(1);
        edge.JsonPath.ShouldBe("$.prerequisites[1].deploymentName");
    }

    [Fact]
    public void Venture_LinksToTheVentureBelowItInAChain()
    {
        var references = Extract("starship-venture",
            """
            {
              "key": "spacecasting-improved",
              "name": "Spacecasting, Improved",
              "prerequisites": [
                { "kind": "venture", "text": "Spacecasting", "ventureName": "Spacecasting" }
              ],
              "description": "The power's range is instead multiplied by 10."
            }
            """);

        var edge = references.ShouldHaveSingleItem();

        edge.Relation.ShouldBe("prerequisiteStarshipVenture");
        edge.TargetType.ShouldBe("starship-venture");
        edge.TargetIdentifier.ShouldBe("Spacecasting");
    }

    /// <summary>
    /// Ammunition points at every launcher that takes it, and each edge needs
    /// its own path or the uniqueness constraint on the reference table would
    /// reject the second one.
    /// </summary>
    [Fact]
    public void Ammunition_LinksToEveryLauncherThatFiresIt()
    {
        var references = Extract("starship-equipment",
            """
            {
              "key": "proton-torpedo",
              "name": "Proton torpedo",
              "category": "ammunition",
              "costInCredits": 650,
              "firedBy": ["Torpedo launcher", "Assault torpedo launcher"]
            }
            """);

        references.Select(reference => reference.TargetIdentifier)
                  .ShouldBe(["Torpedo launcher", "Assault torpedo launcher"]);

        references.Select(reference => reference.JsonPath)
                  .ShouldBe(["$.firedBy[0]", "$.firedBy[1]"]);

        references.ShouldAllBe(reference =>
            reference.Relation == "ammunitionLauncher" &&
            reference.TargetType == "starship-equipment");
    }

    /// <summary>
    /// A launcher has no <c>firedBy</c> of its own, and a base size, deployment
    /// or rule chapter has no prerequisite list at all. None of them may invent
    /// an edge beyond the source they were printed in.
    /// </summary>
    [Theory]
    [InlineData("starship-equipment",
        """{ "key": "torpedo-launcher", "name": "Torpedo launcher", "sourceKey": "sotg", "category": "weapon" }""")]
    [InlineData("starship-base-size",
        """{ "key": "small", "name": "Small", "sourceKey": "sotg", "roles": [{ "name": "Bomber", "armor": "Reinforced" }] }""")]
    [InlineData("starship-deployment",
        """{ "key": "gunner", "name": "Gunner", "sourceKey": "sotg", "role": "Controls one or more weapon emplacements" }""")]
    [InlineData("starship-rule",
        """{ "key": "combat", "title": "Combat", "sourceKey": "sotg", "chapterNumber": 9, "body": "# Chapter 9" }""")]
    public void DocumentWithNoLinks_ProducesOnlyItsSourceEdge(string typeKey, string json)
    {
        var references = Extract(typeKey, json);

        var edge = references.ShouldHaveSingleItem();

        edge.Relation.ShouldBe("source");
        edge.TargetIdentifier.ShouldBe("sotg");
        edge.TargetKind.ShouldBe(ContentReferenceTargetKind.Key);
    }
}
