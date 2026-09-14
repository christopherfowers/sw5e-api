using Shouldly;

using Sw5e.Domain.Content;

namespace Sw5e.Domain.Tests;

/// <summary>
/// Whether a book's chapters still form a path somebody can walk.
/// </summary>
/// <remarks>
/// <para>
/// These are the same three invariants the content repository asserts over the
/// committed corpus. They are repeated here rather than shared because the two
/// answer different questions at different moments: that one asks whether what
/// is committed is sound, and fails a build; this one asks whether what
/// somebody just published is sound, and tells them.
/// </para>
/// <para>
/// The reason this exists at all is that making the reading order authorable
/// moved it out of reach of the only thing checking it. An editor working
/// through the site never runs the repository's test suite, so until now they
/// could publish two chapter fours and find out when a reader noticed.
/// </para>
/// </remarks>
public sealed class ReadingPathTests
{
    private static PlacedChapter At(int order, string group, string key) =>
        new(key, order, group);

    /// <summary>A sound four-chapter path, for the tests that break one thing.</summary>
    private static PlacedChapter[] Sound() =>
    [
        At(1, "Start here", "introduction"),
        At(2, "Start here", "whats-different"),
        At(3, "Playing the game", "combat"),
        At(4, "Playing the game", "casting"),
    ];

    /// <summary>
    /// A path with nothing wrong with it says nothing.
    /// </summary>
    /// <remarks>
    /// The assertion the rest depend on. A check that always finds something is
    /// a banner an author learns to dismiss without reading, which is worse
    /// than no check at all because it looks like diligence.
    /// </remarks>
    [Fact]
    public void ASoundPathIsSilent() => ReadingPath.Inspect(Sound()).ShouldBeEmpty();

    [Fact]
    public void AnEmptyBookIsSilent() => ReadingPath.Inspect([]).ShouldBeEmpty();

    /// <summary>
    /// A book where nobody has placed anything is not a broken path.
    /// </summary>
    /// <remarks>
    /// Most content types are like this and always will be. A weapon has no
    /// place in a reading order, and reporting every one of them as an unwalked
    /// path would bury the cases that matter under several thousand that do not.
    /// </remarks>
    [Fact]
    public void ABookWithNoPlacedChaptersIsSilent() =>
        ReadingPath.Inspect([
            new("flanking", null, null),
            new("cleaving", null, null),
        ]).ShouldBeEmpty();

    /// <summary>
    /// Two chapters at the same position.
    /// </summary>
    /// <remarks>
    /// The failure worth catching most, because it does not present as one.
    /// Both chapters render, in whatever order the tie break produces, so the
    /// first anybody knows is a reader meeting the combat chapter before the
    /// one that explains dice.
    /// </remarks>
    [Fact]
    public void TwoChaptersAtOnePositionAreReported()
    {
        var notices = ReadingPath.Inspect([
            At(1, "Start here", "introduction"),
            At(2, "Start here", "whats-different"),
            At(2, "Playing the game", "combat"),
        ]);

        notices.Count.ShouldBe(1);
        notices[0].Code.ShouldBe(ReadingPath.Codes.DuplicatePosition);

        // Both names, because fixing this means deciding which of the two moves.
        notices[0].Message.ShouldContain("whats-different");
        notices[0].Message.ShouldContain("combat");
    }

    /// <summary>
    /// A position nothing sits at.
    /// </summary>
    [Fact]
    public void AGapInTheMiddleIsReported()
    {
        var notices = ReadingPath.Inspect([
            At(1, "Start here", "introduction"),
            At(3, "Playing the game", "combat"),
        ]);

        notices.Count.ShouldBe(1);
        notices[0].Code.ShouldBe(ReadingPath.Codes.PositionGap);
        notices[0].Message.ShouldContain("2");
    }

    /// <summary>
    /// A path that does not begin at one has lost its opening chapter.
    /// </summary>
    /// <remarks>
    /// Counted from 1 rather than from whatever the book happens to start at,
    /// because "the positions are fine, they just start late" is the wrong
    /// answer to give somebody whose introduction has gone missing.
    /// </remarks>
    [Fact]
    public void APathThatStartsAtTwoIsReported()
    {
        var notices = ReadingPath.Inspect([
            At(2, "Start here", "whats-different"),
            At(3, "Playing the game", "combat"),
        ]);

        notices.Count.ShouldBe(1);
        notices[0].Code.ShouldBe(ReadingPath.Codes.PositionGap);
        notices[0].Message.ShouldContain("1");
    }

    /// <summary>
    /// Building a book in order never complains.
    /// </summary>
    /// <remarks>
    /// The noise test. An author adding chapters one at a time from the start
    /// passes through every prefix of a sound path, and a gap check that fired
    /// on those would warn on almost every publish of a new book — the exact
    /// way a useful signal becomes one people click past.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryPrefixOfASoundPathIsAlsoSilent(int published) =>
        ReadingPath.Inspect(Sound()[..published]).ShouldBeEmpty();

    /// <summary>
    /// A heading whose chapters are split up by another heading's.
    /// </summary>
    /// <remarks>
    /// Headings are drawn in the order of their earliest member, so grouping
    /// and sequence are one decision rather than two that can disagree. That
    /// holds only while a heading's chapters are contiguous: interleave two and
    /// the site must either reorder them, contradicting the authored positions,
    /// or draw the same heading twice.
    /// </remarks>
    [Fact]
    public void AHeadingSplitInTwoIsReported()
    {
        var notices = ReadingPath.Inspect([
            At(1, "Start here", "introduction"),
            At(2, "Playing the game", "combat"),
            At(3, "Start here", "whats-different"),
        ]);

        notices.Count.ShouldBe(1);
        notices[0].Code.ShouldBe(ReadingPath.Codes.InterleavedHeading);
        notices[0].Message.ShouldContain("Start here");
    }

    /// <summary>
    /// A heading repeated in three places is still one notice.
    /// </summary>
    /// <remarks>
    /// One heading is one problem with one fix, whatever the count. Three
    /// notices saying the same thing about the same heading would make it look
    /// like three decisions to make.
    /// </remarks>
    [Fact]
    public void AHeadingSplitThreeWaysIsStillOneNotice()
    {
        var notices = ReadingPath.Inspect([
            At(1, "A", "one"),
            At(2, "B", "two"),
            At(3, "A", "three"),
            At(4, "B", "four"),
            At(5, "A", "five"),
        ]);

        notices.Count(notice => notice.Code == ReadingPath.Codes.InterleavedHeading).ShouldBe(2);
    }

    /// <summary>
    /// An unplaced chapter is not on the path and does not break it.
    /// </summary>
    [Fact]
    public void AnUnplacedChapterIsIgnored()
    {
        var chapters = Sound().Append(new PlacedChapter("changelog", null, null)).ToArray();

        ReadingPath.Inspect(chapters).ShouldBeEmpty();
    }

    /// <summary>
    /// A placed chapter with no heading does not count as interleaving.
    /// </summary>
    /// <remarks>
    /// It is its own problem — the site has nowhere to draw it — but reporting
    /// it as splitting the heading either side of it would name two chapters
    /// that are not at fault and send somebody to the wrong document.
    /// </remarks>
    [Fact]
    public void APlacedChapterWithNoHeadingDoesNotSplitTheOneAroundIt()
    {
        var notices = ReadingPath.Inspect([
            At(1, "Start here", "introduction"),
            new("orphan", 2, null),
            At(3, "Start here", "whats-different"),
        ]);

        notices.ShouldBeEmpty();
    }

    /// <summary>
    /// Independent faults are reported together.
    /// </summary>
    /// <remarks>
    /// An author told about one of two problems fixes one and believes they are
    /// finished.
    /// </remarks>
    [Fact]
    public void IndependentFaultsAreReportedTogether()
    {
        var notices = ReadingPath.Inspect([
            At(2, "Start here", "introduction"),
            At(2, "Start here", "whats-different"),
            At(4, "Playing the game", "combat"),
        ]);

        notices.Select(notice => notice.Code).ShouldBe(
            [ReadingPath.Codes.DuplicatePosition, ReadingPath.Codes.PositionGap],
            ignoreOrder: true);
    }

    /// <summary>
    /// A duplicate position holds back the heading check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two chapters at one position have no defined order between them, so the
    /// sequence read here is settled by a tie break rather than by anybody. Two
    /// such chapters will usually carry different headings, which makes a run
    /// look split — and that report would be the duplicate's shadow, naming
    /// chapters that are not at fault.
    /// </para>
    /// <para>
    /// This fixture is exactly that shape: "Start here" appears to be split by
    /// "Playing the game", but only because the tie break at position 2 puts
    /// them in that order. Reporting the duplicate alone is the honest answer,
    /// and once the positions settle the next publish can say whether the
    /// headings are genuinely interleaved.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADuplicatePositionHoldsBackTheHeadingCheck()
    {
        // Ambiguous: "combat" and "whats-different" both sit at 2, and which
        // comes first is a tie break rather than a decision.
        var ambiguous = ReadingPath.Inspect([
            At(1, "Start here", "introduction"),
            At(2, "Start here", "whats-different"),
            At(2, "Playing the game", "combat"),
        ]);

        ambiguous.Select(notice => notice.Code)
            .ShouldBe([ReadingPath.Codes.DuplicatePosition]);

        /*
          The same three chapters with the duplicate resolved, and nothing else
          changed. Now the sequence is somebody's decision rather than a tie
          break, the headings really are interleaved, and it is reported.

          Asserted as a pair because suppression on its own is indistinguishable
          from the check being broken: a version that never reported an
          interleave at all would pass the first half and fail nothing.
        */
        var settled = ReadingPath.Inspect([
            At(1, "Start here", "introduction"),
            At(2, "Playing the game", "combat"),
            At(3, "Start here", "whats-different"),
        ]);

        settled.Select(notice => notice.Code)
            .ShouldBe([ReadingPath.Codes.InterleavedHeading]);
    }
}
