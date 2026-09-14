namespace Sw5e.Domain.Content;

/// <summary>One chapter's place on a book's reading path.</summary>
/// <param name="Key">The document key, so a notice can name the chapter.</param>
/// <param name="Order">Its position, or null when nobody has placed it.</param>
/// <param name="ReadingGroup">The heading it is read under, or null.</param>
public readonly record struct PlacedChapter(string Key, int? Order, string? ReadingGroup);

/// <summary>
/// Whether a book's chapters still form a path somebody can walk.
/// </summary>
/// <remarks>
/// <para>
/// The order a reader is walked through a book is authored: two fields on each
/// chapter, a position and a heading, both edited as content. That was the
/// point — the person who owns the corpus rearranges the book without anybody
/// touching the site.
/// </para>
/// <para>
/// It also moved the only thing checking those fields out of reach. The
/// invariants live in a test in the content repository, which runs when
/// somebody commits to that repository. An editor working through the site
/// never runs it, so they could publish two chapter fours and find out when a
/// reader noticed. Making the ordering authorable without moving its guard to
/// where the editing happens left the editor less protected than the repository
/// is, and this closes that.
/// </para>
/// <para>
/// Deliberately a pure function over the whole set. These are properties of a
/// book rather than of a document — no schema can express them, because a
/// schema sees one file — and keeping the reasoning away from the database
/// means it can be tested exhaustively and quickly, without a container.
/// </para>
/// <para>
/// Notices, never refusals. Swapping two chapters means one of them transiently
/// shares a position with the other, so refusing a duplicate would make the
/// commonest edit of all impossible to perform.
/// </para>
/// </remarks>
public static class ReadingPath
{
    /// <summary>Codes this check can report.</summary>
    public static class Codes
    {
        /// <summary>Two chapters claim the same position.</summary>
        public const string DuplicatePosition = "duplicate-position";

        /// <summary>The positions are not a run from one.</summary>
        public const string PositionGap = "position-gap";

        /// <summary>A heading's chapters are not next to each other.</summary>
        public const string InterleavedHeading = "interleaved-heading";
    }

    /// <summary>
    /// What is wrong with this book's path, if anything.
    /// </summary>
    /// <param name="chapters">
    /// Every chapter of one book, placed or not. Unplaced ones are ignored
    /// rather than reported: a chapter with no position is not on the path, and
    /// that is a legitimate state — a variant rule has no place in a reading
    /// order and should not be made to claim one.
    /// </param>
    public static IReadOnlyList<ContentPublishNotice> Inspect(
        IReadOnlyList<PlacedChapter> chapters)
    {
        var placed = chapters
            .Where(chapter => chapter.Order is not null)
            .OrderBy(chapter => chapter.Order)
            .ThenBy(chapter => chapter.Key, StringComparer.Ordinal)
            .ToArray();

        if (placed.Length == 0)
        {
            return [];
        }

        var notices = new List<ContentPublishNotice>();

        var duplicates = DuplicatePositions(placed).ToArray();

        notices.AddRange(duplicates);
        notices.AddRange(Gaps(placed));

        /*
          Headings are only checked once the positions are unambiguous.

          Two chapters at one position have no defined order between them, so
          the sequence this reads runs is decided by a tie break rather than by
          anybody. Two chapters sharing a position will usually have different
          headings, which makes the run look split — and reporting that would
          hand somebody a second problem that is really the first one's shadow,
          pointing at chapters that are not at fault.

          So the duplicate is reported and the heading check waits. Once the
          positions are settled, the next publish says whether the headings
          really are interleaved.
        */
        if (duplicates.Length == 0)
        {
            notices.AddRange(InterleavedHeadings(placed));
        }

        return notices;
    }

    /// <summary>
    /// Two chapters at the same position.
    /// </summary>
    /// <remarks>
    /// The failure worth catching most, because it does not look like one. A
    /// duplicate renders — in whatever order the tie break happens to produce —
    /// so the first anybody knows is a reader meeting the combat chapter before
    /// the one explaining dice.
    /// </remarks>
    private static IEnumerable<ContentPublishNotice> DuplicatePositions(PlacedChapter[] placed) =>
        placed
            .GroupBy(chapter => chapter.Order!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => new ContentPublishNotice(
                Codes.DuplicatePosition,
                $"Position {group.Key} is claimed by " +
                $"{Join(group.Select(chapter => chapter.Key))}. " +
                "Whichever the site draws first is a coin toss.",
                "$.order"));

    /// <summary>
    /// A position nothing sits at.
    /// </summary>
    /// <remarks>
    /// Including one below the lowest, which is why this counts from 1 rather
    /// than from whatever the book starts at: a path beginning at 2 has lost
    /// its first chapter, and saying "the positions are fine, they just start
    /// late" would be the wrong answer.
    /// <para>
    /// This is quiet during ordinary authoring. Adding chapters in order never
    /// produces a gap — only publishing out of order does, and then it is
    /// telling somebody something true.
    /// </para>
    /// </remarks>
    private static IEnumerable<ContentPublishNotice> Gaps(PlacedChapter[] placed)
    {
        var taken = placed.Select(chapter => chapter.Order!.Value).ToHashSet();
        var highest = taken.Max();

        var missing = Enumerable.Range(1, highest).Where(position => !taken.Contains(position)).ToArray();

        if (missing.Length == 0)
        {
            yield break;
        }

        yield return new ContentPublishNotice(
            Codes.PositionGap,
            $"Nothing sits at {(missing.Length == 1 ? "position" : "positions")} " +
            $"{Join(missing.Select(position => position.ToString()))}, " +
            $"and the path runs to {highest}.",
            "$.order");
    }

    /// <summary>
    /// A heading whose chapters are not next to each other.
    /// </summary>
    /// <remarks>
    /// Headings are drawn in the order of their earliest member, so the
    /// grouping and the sequence are one decision rather than two that can
    /// disagree. That only holds while a heading's chapters are contiguous:
    /// interleave two and the site must either reorder them, contradicting the
    /// authored positions, or draw the same heading twice.
    /// </remarks>
    private static IEnumerable<ContentPublishNotice> InterleavedHeadings(PlacedChapter[] placed)
    {
        var runs = new List<string>();

        foreach (var chapter in placed)
        {
            var heading = chapter.ReadingGroup;

            if (string.IsNullOrWhiteSpace(heading))
            {
                continue;
            }

            if (runs.Count == 0 || runs[^1] != heading)
            {
                runs.Add(heading);
            }
        }

        return runs
            .GroupBy(heading => heading, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new ContentPublishNotice(
                Codes.InterleavedHeading,
                $"\"{group.Key}\" appears in {group.Count()} separate places on the path, " +
                "so its chapters are split up by another heading's. The site would " +
                "draw that heading more than once.",
                "$.readingGroup"));
    }

    /// <summary>
    /// A readable list: "a and b", or "a, b and c".
    /// </summary>
    /// <remarks>
    /// Worth the few lines. These sentences are read by somebody deciding what
    /// to go and fix, and "a, b" reads as a fragment where "a and b" reads as
    /// a statement.
    /// </remarks>
    private static string Join(IEnumerable<string> values)
    {
        var all = values.ToArray();

        return all.Length switch
        {
            0 => string.Empty,
            1 => all[0],
            _ => $"{string.Join(", ", all[..^1])} and {all[^1]}",
        };
    }
}
