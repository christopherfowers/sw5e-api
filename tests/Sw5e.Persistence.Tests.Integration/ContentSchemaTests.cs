using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Sw5e.Domain.Content;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Persistence.Tests.Integration;

/// <summary>
/// What the migration actually builds, asserted against the running database
/// rather than against the model that generated it.
/// </summary>
/// <remarks>
/// Asserting on the model would only prove that EF agrees with itself. Every
/// property checked here — that a check constraint refuses a bad row, that the
/// text columns carry the C collation, that the trigram indexes exist and use
/// the right operator class — is a property of the schema PostgreSQL ended up
/// with, and each of them can be broken by a change that leaves the model
/// looking correct.
/// </remarks>
public sealed class ContentSchemaTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    protected override string DatabaseName => "schema_tests";

    protected override bool ImportContent => false;

    [DockerFact]
    public async Task Migrate_CreatesEveryTableInTheContentSchema()
    {
        var tables = await QueryAsync(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'content' AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """);

        tables.ShouldBe([
            "__EFMigrationsHistory",
            "content_draft",
            "content_item",
            "content_reference",
            "content_revision",
            "content_type",
        ]);
    }

    /// <summary>
    /// The revision history refuses to be edited, and the refusal comes from
    /// the database rather than from the code above it.
    /// </summary>
    /// <remarks>
    /// Asserted at this level, on a bare migrated schema, because the guard is
    /// a property of the schema. The API suite proves the same thing through a
    /// row it published; this proves the migration installs the trigger at all,
    /// which is what a deployment gets.
    /// </remarks>
    [DockerFact]
    public async Task Migrate_MakesTheRevisionHistoryAppendOnly()
    {
        await ExecuteAsync(
            """
            INSERT INTO content.content_revision
                (content_type, item_key, number, name, body, version, action,
                 schema_version, created_at)
            VALUES
                ('species', 'append-only-probe', 1, 'Probe', '{}'::jsonb, 'v0',
                 'imported', 1, now())
            """);

        var update = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync(
                "UPDATE content.content_revision SET name = 'edited' WHERE item_key = 'append-only-probe'"));

        update.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        var delete = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync(
                "DELETE FROM content.content_revision WHERE item_key = 'append-only-probe'"));

        delete.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        var names = await QueryAsync(
            "SELECT name FROM content.content_revision WHERE item_key = 'append-only-probe'");

        names.ShouldBe(["Probe"]);
    }

    /// <summary>
    /// The migration history belongs to the content schema, not to
    /// <c>public</c>.
    /// </summary>
    /// <remarks>
    /// This is what leaves room for a second context. Identity will have its
    /// own schema and its own migrations; if both contexts recorded their
    /// history in <c>public.__EFMigrationsHistory</c> they would each read the
    /// other's rows as migrations of their own that had already been applied,
    /// and the next <c>Migrate</c> on either would try to create tables that
    /// exist. Nothing about the content schema looks wrong when that happens —
    /// it breaks the other feature, months later, which is exactly the kind of
    /// thing nobody thinks to test.
    /// </remarks>
    [DockerFact]
    public async Task Migrate_RecordsItsHistoryInsideTheContentSchemaOnly()
    {
        var inContent = await Database.ScalarAsync<long>(
            "SELECT count(*) FROM content.\"__EFMigrationsHistory\"");

        inContent.ShouldBeGreaterThan(0);

        var inPublic = await Database.ScalarAsync<bool>(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'
            )
            """);

        inPublic.ShouldBeFalse(
            "a history table in public would be shared with the identity context");
    }

    [DockerFact]
    public async Task Migrate_LeavesNothingPending()
    {
        await using var database = Database.CreateContext();

        (await database.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await database.Database.GetAppliedMigrationsAsync()).ShouldNotBeEmpty();
    }

    /// <summary>
    /// The seeded registry has to match the compiled one exactly, because the
    /// compiled one is what the type column's foreign key is checked against.
    /// </summary>
    [DockerFact]
    public async Task Migrate_SeedsTheTypeRegistryFromTheCompiledOne()
    {
        await using var database = Database.CreateContext();

        var seeded = await database.ContentTypes
            .OrderBy(type => type.SortOrder)
            .Select(type => new { type.Key, type.DisplayName, type.PluralName, type.RouteSegment })
            .ToListAsync();

        seeded.Select(type => type.Key)
              .ShouldBe(ContentTypeRegistry.All.Select(definition => definition.Key));

        seeded.Select(type => type.RouteSegment)
              .ShouldBe(ContentTypeRegistry.All.Select(definition => definition.RouteSegment));

        seeded.Select(type => type.PluralName)
              .ShouldBe(ContentTypeRegistry.All.Select(definition => definition.PluralName));
    }

    /// <summary>
    /// Every text column carries the C collation.
    /// </summary>
    /// <remarks>
    /// Without it, ordering depends on the locale the database was created
    /// with, and the two content stores return the same page of species in
    /// different orders on some hosts and the same order on others. That is the
    /// worst kind of defect: it passes on the developer's machine, passes in
    /// CI, and shows up as "the list is in a weird order" on one deployment
    /// with nothing to connect it to a code change. jsonb is excluded because
    /// it is not a collatable type.
    /// </remarks>
    [DockerFact]
    public async Task TextColumns_AreOrderedByByteValueRatherThanByLocale()
    {
        var uncollated = await QueryAsync(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'content'
              AND table_name IN ('content_item', 'content_reference', 'content_type')
              AND data_type IN ('character varying', 'text')
              AND (collation_name IS DISTINCT FROM 'C')
            ORDER BY 1
            """);

        uncollated.ShouldBeEmpty();

        // The negative half of the assertion: if the query above matched
        // nothing because it was written wrongly, this would be empty too.
        var collated = await QueryAsync(
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'content' AND collation_name = 'C'
            ORDER BY 1
            """);

        collated.ShouldContain("content_item.name_lower");
        collated.ShouldContain("content_item.item_key");
        collated.ShouldContain("content_item.search_text_lower");
    }

    /// <summary>
    /// The ordering the list endpoint uses has to be the one PostgreSQL
    /// actually produces, not the one it produces under a locale collation.
    /// </summary>
    /// <remarks>
    /// The three names below are chosen because they order differently under
    /// <c>en_US.utf8</c>, which ignores the apostrophe for collation purposes,
    /// than they do byte by byte. Under a locale collation "Twi'lek" sorts
    /// before "Twilight"; byte-wise the apostrophe (0x27) puts it first
    /// regardless, but "twi'lek" versus "twia" is the pair that separates them.
    /// This asserts on the outcome rather than on the metadata, so it catches a
    /// collation that was declared and then overridden.
    /// </remarks>
    [DockerFact]
    public async Task Ordering_PutsPunctuationWhereByteOrderPutsIt()
    {
        var ordered = await QueryAsync(
            """
            SELECT value FROM (VALUES ('twi''lek'), ('twia'), ('twib')) AS t(value)
            ORDER BY value COLLATE "C"
            """);

        // Byte order: apostrophe (0x27) is below every letter, so it leads.
        ordered.ShouldBe(["twi'lek", "twia", "twib"]);

        ordered.ShouldBe(
            ordered.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            "the database's ordering must agree with StringComparer.Ordinal");
    }

    [DockerFact]
    public async Task Schema_HasTrigramIndexesOnTheColumnsSubstringSearchScans()
    {
        var names = await QueryAsync(
            """
            SELECT indexname FROM pg_indexes
            WHERE schemaname = 'content' AND indexname LIKE '%trgm%'
            ORDER BY indexname
            """);

        // Named rather than counted. A count says only that somebody added or
        // removed one; the names say which column stopped being searchable,
        // which is the thing a reader of a failure needs to know.
        names.ShouldBe([
            "ix_content_item_heading_text_trgm",
            "ix_content_item_name_lower_trgm",
            "ix_content_item_search_text_trgm",
        ]);

        var definitions = await QueryAsync(
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'content' AND indexname LIKE '%trgm%'
            ORDER BY indexname
            """);

        // GIN over trigrams is the only index shape that serves a leading
        // wildcard, and every one of these columns is only ever queried that
        // way. A b-tree here would be an index the planner never uses.
        definitions.ShouldAllBe(definition => definition.Contains("USING gin"));
        definitions.ShouldAllBe(definition => definition.Contains("gin_trgm_ops"));
    }

    /// <summary>
    /// A key that is not a slug cannot be stored, whatever writes it.
    /// </summary>
    /// <remarks>
    /// The endpoint validates <c>{key}</c> and the importer validates a file
    /// name, but neither of those is the last word: a future authoring API, a
    /// bulk fix applied by hand, or a restore from a damaged dump all reach the
    /// table directly. A row whose key is not a slug is unreachable through the
    /// API that is supposed to serve it, so it is refused at the column.
    /// </remarks>
    [DockerTheory]
    [InlineData("../etc/passwd")]
    [InlineData("Wookiee")]
    [InlineData("with space")]
    [InlineData("trailing-")]
    [InlineData("")]
    public async Task Schema_RefusesAnItemKeyThatIsNotASlug(string key)
    {
        var exception = await Should.ThrowAsync<PostgresException>(() => InsertItemAsync("species", key));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBe("ck_content_item_key_slug");
    }

    [DockerFact]
    public async Task Schema_AcceptsAWellFormedSlug()
    {
        // The paired positive case. Without it, the theory above would pass
        // against a table that refused every insert for some unrelated reason.
        await Should.NotThrowAsync(() => InsertItemAsync("species", "some-valid-key"));
    }

    [DockerTheory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task Schema_RefusesABodyThatIsNotAJsonObject(string body)
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertItemAsync("species", "valid-key", body: body));

        exception.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        exception.ConstraintName.ShouldBe("ck_content_item_body_is_object");
    }

    [DockerFact]
    public async Task Schema_RefusesASecondItemOfTheSameTypeWithTheSameKey()
    {
        await InsertItemAsync("species", "duplicated");

        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertItemAsync("species", "duplicated"));

        exception.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [DockerFact]
    public async Task Schema_AllowsTheSameKeyUnderTwoDifferentTypes()
    {
        // Identity is (type, key), not key alone. A power and a feat may
        // legitimately share a slug, and a unique constraint on the key alone
        // would refuse the second one with no way to tell it apart from a real
        // duplicate.
        await InsertItemAsync("power", "shared-slug");

        await Should.NotThrowAsync(() => InsertItemAsync("feat", "shared-slug"));
    }

    /// <summary>
    /// The type column is constrained to the registry, so a row cannot exist
    /// under a type the API will never ask for.
    /// </summary>
    [DockerFact]
    public async Task Schema_RefusesAnItemOfATypeThatIsNotInTheRegistry()
    {
        var exception = await Should.ThrowAsync<PostgresException>(
            () => InsertItemAsync("starship", "x-wing"));

        exception.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    /// <summary>
    /// Deleting an item takes its outgoing edges with it and leaves the edges
    /// that pointed at it as unresolved intent.
    /// </summary>
    /// <remarks>
    /// This is the difference between a reference table and a foreign key. If
    /// removing a power deleted every edge that named it, the information that
    /// something still requires it would be gone, and re-authoring the power
    /// would not bring the link back. Set-null keeps the edge, and the
    /// importer's resolution pass reconnects it.
    /// </remarks>
    [DockerFact]
    public async Task Schema_KeepsAnEdgeWhenItsTargetIsDeleted()
    {
        await Database.ImportAsync();

        await using (var database = Database.CreateContext())
        {
            var target = await database.ContentItems
                .SingleAsync(item => item.ContentType == "power" && item.ItemKey == "force-push");

            database.ContentItems.Remove(target);
            await database.SaveChangesAsync();
        }

        await using var reread = Database.CreateContext();

        var edge = await reread.ContentReferences.SingleAsync(
            reference => reference.Relation == "prerequisitePower" &&
                         reference.TargetIdentifier == "Force Push");

        edge.ResolvedItemId.ShouldBeNull("the target is gone, but the intent to link to it is not");
    }

    private Task InsertItemAsync(string type, string key, string body = "{}") =>
        ExecuteAsync(
            """
            INSERT INTO content.content_item
                (content_type, item_key, name, facets, body, search_text, version,
                 name_lower, search_text_lower, created_at, updated_at)
            VALUES (@type, @key, 'Name', '{}', @body::jsonb, '', 'v', 'name', '', now(), now())
            """,
            ("type", type),
            ("key", key),
            ("body", body));

    private async Task ExecuteAsync(string sql, params (string Name, string Value)[] parameters)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = await Database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<string>();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
