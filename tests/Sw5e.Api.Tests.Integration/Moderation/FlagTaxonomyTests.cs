using Shouldly;
using Sw5e.Domain.Moderation;

namespace Sw5e.Api.Tests.Integration.Moderation;

/// <summary>
/// The two tables the whole feature is built on: which reason belongs to which
/// kind of thing, and which status may follow which.
/// </summary>
/// <remarks>
/// No database and no host. These are decisions, and a decision can be wrong
/// without a server being involved — but they are the decisions that the store,
/// the endpoint and the browser client all read, so getting one wrong is wrong
/// in three places at once.
/// </remarks>
public sealed class FlagTaxonomyTests
{
    /// <summary>
    /// The published spelling of every reason, written out.
    /// </summary>
    /// <remarks>
    /// Pinned as literals rather than derived from the source it is checking,
    /// which would make the test agree with whatever the code says. These
    /// strings are stored in a database column and switched on by a browser
    /// client, so changing one is a data migration and a client release; this
    /// is what makes that a decision somebody has to make on purpose.
    /// </remarks>
    public static TheoryData<FlagReason, string> ReasonNames =>
        new()
        {
            { FlagReason.ImageArtistKnown, "image-artist-known" },
            { FlagReason.ImageAttributionMissing, "image-attribution-missing" },
            { FlagReason.ImageReplacementWanted, "image-replacement-wanted" },
            { FlagReason.ImageRightsComplaint, "image-rights-complaint" },
            { FlagReason.ImageWrongSubject, "image-wrong-subject" },
            { FlagReason.TextError, "text-error" },
            { FlagReason.ContentIncorrect, "content-incorrect" },
            { FlagReason.ContentMissing, "content-missing" },
            { FlagReason.SourceAttribution, "source-attribution" },
            { FlagReason.Other, "other" },
        };

    [Theory]
    [MemberData(nameof(ReasonNames))]
    public void EveryReasonHasItsPublishedSpelling(FlagReason reason, string name)
    {
        FlagWire.NameOf(reason).ShouldBe(name);

        FlagWire.TryParseReason(name, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reason);
    }

    [Fact]
    public void EveryReasonInTheEnumHasASpelling()
    {
        // The pinned table above would still pass if a reason were added and
        // forgotten. This is the half that notices.
        foreach (var reason in Enum.GetValues<FlagReason>())
        {
            Should.NotThrow(() => FlagWire.NameOf(reason));
        }

        FlagWire.ReasonNames.Count.ShouldBe(Enum.GetValues<FlagReason>().Length);
    }

    [Fact]
    public void ParsingIsExactRatherThanForgiving()
    {
        // The column these land in compares byte for byte, and the outstanding
        // duplicate index compares along with it. Accepting two spellings of
        // one reason would mean duplicate suppression stops working for anybody
        // who shouted.
        FlagWire.TryParseReason("Text-Error", out _).ShouldBeFalse();
        FlagWire.TryParseReason("TEXT-ERROR", out _).ShouldBeFalse();
        FlagWire.TryParseReason("text_error", out _).ShouldBeFalse();
        FlagWire.TryParseReason(" text-error ", out _).ShouldBeFalse();
        FlagWire.TryParseReason(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void EveryReasonBelongsToOneKindExceptOther()
    {
        // A reason that could be raised against either would put a report
        // nobody can act on in the queue: "I know who drew this" against a
        // rules chapter has no meaning. `other` is the one exception, and it
        // takes its kind from the target instead of imposing one.
        foreach (var reason in Enum.GetValues<FlagReason>())
        {
            var kind = ContentFlagRules.KindOf(reason);

            if (reason == FlagReason.Other)
            {
                kind.ShouldBeNull();
                ContentFlagRules.Permits(reason, FlagTargetKind.Image).ShouldBeTrue();
                ContentFlagRules.Permits(reason, FlagTargetKind.Document).ShouldBeTrue();
                continue;
            }

            kind.ShouldNotBeNull();

            var other = kind == FlagTargetKind.Image
                ? FlagTargetKind.Document
                : FlagTargetKind.Image;

            ContentFlagRules.Permits(reason, kind.Value).ShouldBeTrue();
            ContentFlagRules.Permits(reason, other).ShouldBeFalse();
        }
    }

    [Fact]
    public void TheTwoOfferedListsCoverEveryReasonBetweenThem()
    {
        // What the browser client renders in its two menus. A reason missing
        // from both would exist on the server and be unreachable from the site,
        // which is the kind of gap nothing else would notice.
        ContentFlagRules.ReasonsForImages
            .Concat(ContentFlagRules.ReasonsForDocuments)
            .Distinct()
            .OrderBy(reason => reason)
            .ShouldBe(Enum.GetValues<FlagReason>().OrderBy(reason => reason));

        ContentFlagRules.ReasonsForImages.ShouldContain(FlagReason.Other);
        ContentFlagRules.ReasonsForDocuments.ShouldContain(FlagReason.Other);
    }

    [Fact]
    public void OnlyOtherDemandsAnExplanation()
    {
        foreach (var reason in Enum.GetValues<FlagReason>())
        {
            ContentFlagRules.RequiresDetails(reason)
                .ShouldBe(reason == FlagReason.Other);
        }
    }

    [Theory]
    [InlineData(FlagStatus.Open, FlagStatus.Accepted)]
    [InlineData(FlagStatus.Open, FlagStatus.Declined)]
    [InlineData(FlagStatus.Accepted, FlagStatus.Resolved)]
    [InlineData(FlagStatus.Accepted, FlagStatus.Declined)]
    [InlineData(FlagStatus.Accepted, FlagStatus.Open)]
    [InlineData(FlagStatus.Declined, FlagStatus.Open)]
    [InlineData(FlagStatus.Resolved, FlagStatus.Open)]
    public void TheseMovesAreAllowed(FlagStatus from, FlagStatus to) =>
        ContentFlagRules.CanTransition(from, to).ShouldBeTrue();

    [Theory]
    // The one that matters most: "resolved" claims work was done on something a
    // reviewer had just said needed none.
    [InlineData(FlagStatus.Declined, FlagStatus.Resolved)]
    // Open straight to resolved skips the state that records agreement, which
    // is the state the whole worklist is built on.
    [InlineData(FlagStatus.Open, FlagStatus.Resolved)]
    // Restating the current status is almost always a double submit or two
    // reviewers on one row.
    [InlineData(FlagStatus.Open, FlagStatus.Open)]
    [InlineData(FlagStatus.Accepted, FlagStatus.Accepted)]
    [InlineData(FlagStatus.Declined, FlagStatus.Declined)]
    [InlineData(FlagStatus.Resolved, FlagStatus.Resolved)]
    [InlineData(FlagStatus.Resolved, FlagStatus.Declined)]
    [InlineData(FlagStatus.Resolved, FlagStatus.Accepted)]
    [InlineData(FlagStatus.Declined, FlagStatus.Accepted)]
    public void TheseMovesAreRefused(FlagStatus from, FlagStatus to) =>
        ContentFlagRules.CanTransition(from, to).ShouldBeFalse();

    [Fact]
    public void EveryFinishedStateCanBeReopened()
    {
        // Reviewers are wrong sometimes, and a queue with no way back is one
        // people are afraid to triage quickly — which produces a queue nobody
        // triages at all.
        ContentFlagRules.CanTransition(FlagStatus.Declined, FlagStatus.Open).ShouldBeTrue();
        ContentFlagRules.CanTransition(FlagStatus.Resolved, FlagStatus.Open).ShouldBeTrue();
    }

    [Fact]
    public void OpenAndAcceptedAreTheWorklist()
    {
        ContentFlagRules.IsOutstanding(FlagStatus.Open).ShouldBeTrue();
        ContentFlagRules.IsOutstanding(FlagStatus.Accepted).ShouldBeTrue();
        ContentFlagRules.IsOutstanding(FlagStatus.Declined).ShouldBeFalse();
        ContentFlagRules.IsOutstanding(FlagStatus.Resolved).ShouldBeFalse();
    }

    [Fact]
    public void TheColumnWidthCoversEveryPublishedSpelling()
    {
        // The schema derives its column lengths from this number, so a value
        // added later cannot be silently truncated by a schema written before
        // it existed.
        var longest = FlagWire.ReasonNames
            .Concat(FlagWire.StatusNames)
            .Concat([
                FlagWire.NameOf(FlagTargetKind.Document),
                FlagWire.NameOf(FlagTargetKind.Image),
            ])
            .Max(name => name.Length);

        FlagWire.MaxNameLength.ShouldBe(longest);
    }
}
