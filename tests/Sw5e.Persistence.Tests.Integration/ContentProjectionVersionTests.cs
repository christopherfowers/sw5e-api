using Shouldly;

using Sw5e.Infrastructure.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// The projection table, pinned to the version that describes it.
/// </summary>
/// <remarks>
/// Its own class, with no database fixture, because none of this needs one: it
/// reads a static table and hashes it. That matters beyond tidiness. Every
/// other test in this project starts a PostgreSQL container, so on a machine
/// with no Docker daemon the whole project is unavailable, and this is the one
/// assertion most likely to be wanted exactly then. Changing a projection is a
/// five-second edit, and the guard for it should not require a database to be
/// running.
/// </remarks>
public sealed class ContentProjectionVersionTests
{
    /// <summary>
    /// Changing what a document is projected from means changing the version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the guard for the mistake that actually happened. The importer
    /// skips a document whose version it already holds, so a projection change
    /// reaches only the documents that were edited in the same release. When
    /// headings were harvested into their own column, the release imported
    /// cleanly, reported "175 updated, 7,702 unchanged", and left the new
    /// column empty on 7,876 of 7,877 rows. The feature was inert, and every
    /// test in this suite was green. Because they all import into an empty
    /// database, where every document is an insert and no version is compared.
    /// </para>
    /// <para>
    /// Mixing the projection version into the document hash fixed the
    /// mechanism. It did not stop anybody forgetting to change it, which is the
    /// half that failed. Pinning both together does: edit the projection table
    /// and this fails, naming the version that has to move with it.
    /// </para>
    /// <para>
    /// It fingerprints the table and nothing else. It will not notice a change
    /// in how a field becomes text (the heading harvest, the summary cap) and
    /// that limit is stated in <c>ContentProjection.Fingerprint</c> rather than
    /// left to be discovered. What it removes is the case where the change is
    /// right there in the diff as an edited list, and the version two lines
    /// above it was simply not looked at.
    /// </para>
    /// </remarks>
    [Fact]
    public void ChangingTheProjectionMeansChangingItsVersion()
    {
        ContentProjection.Fingerprint().ShouldBe(
            "aca2a2fb02568649",
            "the projection table changed. Bump ContentProjection.Version and " +
            "put the new fingerprint here, or every document already in a " +
            "database keeps a row built by the old rules and the change reaches " +
            "nothing but whatever happens to be edited alongside it.");

        ContentProjection.Version.ShouldBe("7-the-front-pages-furniture");
    }
}
