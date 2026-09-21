using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// The administrative log cannot be edited, and the database is what refuses.
/// </summary>
/// <remarks>
/// <para>
/// These do not go through the API, deliberately, for the reason the content
/// revision tests give: there is no endpoint that updates or deletes an audit
/// entry, so an HTTP-level test would prove only that something absent is
/// absent and would keep passing after somebody added one. The statements below
/// are the ones a repair script, a data-fix migration or a careless
/// <c>ExecuteDelete</c> would issue, run with the application's own
/// credentials.
/// </para>
/// <para>
/// The reason this table needs the protection more than most: the party with
/// both the database access and the motive to edit a record of administrative
/// actions is an administrator, which is exactly the party the record exists to
/// hold to account. Removing the trigger from the migration turns both of these
/// from a raised exception into a silent success.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AdministrativeLogImmutabilityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task AnAdministrativeActionCannotBeRewritten()
    {
        var id = await RecordOneAsync("audit-immutable-update");

        var failure = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync(
                """
                UPDATE identity."AdministrativeActions"
                SET "ActorDisplayName" = 'Somebody Else'
                WHERE "Id" = @id
                """,
                id));

        // restrict_violation is what the trigger raises. Asserting the code
        // rather than the wording means the test does not break when the
        // message changes and does not pass because some unrelated statement
        // error happened instead.
        failure.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        (await ActorNameAsync(id)).ShouldNotBe("Somebody Else");
    }

    [Fact]
    public async Task AnAdministrativeActionCannotBeDeleted()
    {
        var id = await RecordOneAsync("audit-immutable-delete");

        var failure = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync(
                "DELETE FROM identity.\"AdministrativeActions\" WHERE \"Id\" = @id",
                id));

        failure.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);

        // Still there. An exception with the row gone would be the worst of
        // both.
        (await ActorNameAsync(id)).ShouldNotBeNull();
    }

    /// <summary>
    /// Produces one real entry, by making a real administrative change through
    /// the endpoint.
    /// </summary>
    /// <remarks>
    /// Inserting a row here directly would test the trigger against a row this
    /// test made up. Going through the endpoint tests it against the rows the
    /// application actually writes.
    /// </remarks>
    private async Task<Guid> RecordOneAsync(string label)
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, label);

        var subjectId = await AdministrationFlow.IdOfAsync(
            _factory,
            (await AdministrationFlow.MemberAsync(
                _factory, _factory.CreateBrowserClient(), $"{label}-subject")).EmailAddress);

        (await admin.PutAsJsonAsync(
                $"/api/auth/admin/users/{subjectId}/roles",
                new { roles = new[] { Sw5eRoles.Contributor } }))
            .EnsureSuccessStatusCode();

        var log = await admin.GetAsync($"/api/auth/admin/audit?subjectId={subjectId}");

        return (await log.ReadJsonAsync())
            .GetProperty("actions").EnumerateArray().Single()
            .GetProperty("id").GetGuid();
    }

    private async Task ExecuteAsync(string sql, Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<Sw5eIdentityDbContext>();

        await store.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("id", id));
    }

    private async Task<string?> ActorNameAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<Sw5eIdentityDbContext>();

        return await store.AdministrativeActions
            .AsNoTracking()
            .Where(entry => entry.Id == id)
            .Select(entry => entry.ActorDisplayName)
            .FirstOrDefaultAsync();
    }
}
