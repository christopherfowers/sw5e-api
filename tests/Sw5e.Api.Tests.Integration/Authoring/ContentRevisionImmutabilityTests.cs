using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;
using Sw5e.Api.Tests.Integration.Moderation;
using Sw5e.Identity;
using Sw5e.Infrastructure.Persistence.Content;

namespace Sw5e.Api.Tests.Integration.Authoring;

/// <summary>
/// The audit trail cannot be edited, and the database is what refuses.
/// </summary>
/// <remarks>
/// <para>
/// These do not go through the API, deliberately. There is no endpoint that
/// updates or deletes a revision, so an HTTP-level test would prove only that
/// something absent is absent, and would keep passing after somebody added one.
/// The statements here are the ones a future repair script, data-fix migration
/// or careless <c>ExecuteDelete</c> would issue, run directly against the
/// database with the application's own credentials.
/// </para>
/// <para>
/// Removing the trigger from the authoring migration turns both of these from
/// a raised exception into a silent success.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class ContentRevisionImmutabilityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthoringApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new AuthoringApiFactory(postgres);
        await _factory.ResetContentAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ARevisionCannotBeUpdated()
    {
        var revision = await PublishOneAsync("immutable-update");

        var failure = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync(
                "UPDATE content.content_revision SET reason = 'tampered with' WHERE id = @id",
                revision.Id));

        // restrict_violation is what the trigger raises. Asserting the code
        // rather than the message means the test does not break when the
        // wording changes, and does not pass if some unrelated statement error
        // happens to be thrown instead.
        failure.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        // And the row is untouched.
        var after = await ReadAsync(revision.Id);
        after.ShouldNotBeNull();
        after.Reason.ShouldBe(revision.Reason);
        after.Body.ShouldBe(revision.Body);
    }

    [Fact]
    public async Task ARevisionCannotBeDeleted()
    {
        var revision = await PublishOneAsync("immutable-delete");

        var failure = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync("DELETE FROM content.content_revision WHERE id = @id", revision.Id));

        failure.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        (await ReadAsync(revision.Id)).ShouldNotBeNull();
    }

    /// <summary>Publishes one document and returns the revision it produced.</summary>
    private async Task<ContentRevisionRow> PublishOneAsync(string label)
    {
        var client = _factory.CreateBrowserClient();
        await FlagFlow.SignInWithRoleAsync(_factory, client, label, Sw5eRoles.Administrator);

        var key = AuthoringFlow.NewKey(label);

        await AuthoringFlow.SaveDraftAsync(
            client, key, AuthoringFlow.Valid(key, "Immutable", "Written once, never edited."));

        (await AuthoringFlow.PublishAsync(client, key, "The original reason."))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var revisions = await AuthoringFlow.StoredRevisionsAsync(_factory, key);

        revisions.Count.ShouldBe(1);

        return revisions[0];
    }

    private async Task ExecuteAsync(string sql, long id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        await database.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("id", id));
    }

    private async Task<ContentRevisionRow?> ReadAsync(long id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<Sw5eContentDbContext>();

        return await database.ContentRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(revision => revision.Id == id);
    }
}
