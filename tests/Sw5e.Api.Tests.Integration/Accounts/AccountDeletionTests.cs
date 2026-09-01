using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Sw5e.Api.Tests.Integration.Moderation;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// That deleting an account removes the person and not the record.
/// </summary>
/// <remarks>
/// <para>
/// The decision this suite exists to pin down is
/// <see cref="DeletingAnAccountLeavesWhatItWroteBehind"/>. A revision or a
/// report is the record that somebody changed what a whole community reads, and
/// the revision table is append-only at the database precisely so that the
/// people who can make those changes cannot quietly unmake the record of having
/// made them. A deletion that reached in and erased authorship would be exactly
/// that with a friendlier name, available to any administrator against any
/// contributor at any time — so it does not, and this is where that is proved
/// rather than asserted in a comment.
/// </para>
/// <para>
/// What the reader sees instead is a removed account, which is the honest
/// rendering of "somebody wrote this and is no longer here", and which the flag
/// queue's contract already documented before this feature existed.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AccountDeletionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task DeletingAnAccountRemovesItAndEndsEveryWayBackIn()
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "delete-admin");

        var victimClient = _factory.CreateBrowserClient();
        var victim = await AdministrationFlow.MemberAsync(_factory, victimClient, "delete-victim");
        var victimId = await AdministrationFlow.IdOfAsync(_factory, victim.EmailAddress);

        var deleted = await admin.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"/api/auth/admin/users/{victimId}")
        {
            Content = JsonContent.Create(new { reason = "Asked to be removed." }),
        });

        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await deleted.ReadJsonAsync();

        body.GetProperty("userId").GetGuid().ShouldBe(victimId);

        // The response says so, at the moment it matters, rather than leaving
        // the administrator to find out from a document they read once.
        body.GetProperty("authorshipRetained").GetBoolean().ShouldBeTrue();

        (await AdministrationFlow.ExistsAsync(_factory, victimId)).ShouldBeFalse();

        // The passkey went with the account, so the credential that used to
        // work no longer resolves to anybody.
        (await victim.SignInAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And the session that was open when the account was deleted is gone
        // too. The stamp validator refuses a principal whose account it cannot
        // find, which is the same mechanism that already covered this case
        // before deletion existed as an endpoint.
        (await victimClient.GetAsync("/api/auth/me"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The address is free again, which is what deletion has to mean if it
        // means anything: a person who leaves and comes back is not permanently
        // barred from their own address.
        //
        // Asserted on what was emailed rather than on the status code, because
        // the status code cannot say. Registration answers the same 202 for a
        // free address and for one that is already taken — that is the whole
        // enumeration defence — so the only observable difference is that a
        // free address is sent a verification link while a taken one is sent a
        // recovery link. Before the deletion this address had a verified
        // account and would have been sent the second.
        var before = _factory.Email.CountOf(
            AccountMessageKind.Verification, victim.EmailAddress);

        (await _factory.CreateBrowserClient().PostAsJsonAsync(
                "/api/auth/register",
                new { email = victim.EmailAddress, displayName = "Somebody Else" }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        _factory.Email.CountOf(AccountMessageKind.Verification, victim.EmailAddress)
            .ShouldBe(before + 1);

        _factory.Email.CountOf(AccountMessageKind.Recovery, victim.EmailAddress).ShouldBe(0);
    }

    [Fact]
    public async Task DeletingAnAccountLeavesWhatItWroteBehind()
    {
        // Emptied first, for the reason the flag tests empty it: this
        // collection shares one PostgreSQL container, and an assertion about
        // which reports point at one document is an assertion a leftover from
        // another class can make pass or fail for reasons of its own.
        await FlagFlow.ClearAsync(_factory);

        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "delete-authorship-admin");

        var reporterClient = _factory.CreateBrowserClient();
        var reporter = await AdministrationFlow.MemberAsync(
            _factory, reporterClient, "delete-authorship");

        var reporterId = await AdministrationFlow.IdOfAsync(_factory, reporter.EmailAddress);

        var flagId = await FlagFlow.RaiseAcceptedAsync(
            reporterClient,
            "image-artist-known",
            FlagFlow.ImageType,
            FlagFlow.ImageKey,
            "The portrait is by an artist I can name; here is the commission thread.");

        (await admin.SendAsync(new HttpRequestMessage(
                HttpMethod.Delete, $"/api/auth/admin/users/{reporterId}")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The row is still there, still pointing at the same account
        // identifier. Nothing was reassigned, blanked or rewritten.
        var stored = (await FlagFlow.StoredAsync(_factory)).Single(flag => flag.Id == flagId);

        stored.ReporterUserId.ShouldBe(reporterId);
        stored.Details.ShouldNotBeNull();
        stored.Details.ShouldContain("commission thread");

        // And the queue renders it as a removed account rather than dropping
        // the row. Losing it would lose the reports of exactly the people who
        // left, which on a queue whose reason for existing is attribution
        // knowledge is the worst possible thing to lose.
        var queue = await admin.GetAsync(
            $"/api/flags?status=all&targetType={FlagFlow.ImageType}&targetKey={FlagFlow.ImageKey}" +
            "&pageSize=100");

        queue.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entry = (await queue.ReadJsonAsync())
            .GetProperty("flags").EnumerateArray()
            .Single(flag => flag.GetProperty("id").GetGuid() == flagId);

        entry.GetProperty("reporter").GetProperty("id").GetGuid().ShouldBe(reporterId);
        entry.GetProperty("reporter").GetProperty("displayName").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AnAdministratorCannotDeleteAnAccountThatDoesNotExist()
    {
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "delete-missing-admin");

        // Distinguishable from success, and safe to be: the route already
        // requires the administrator role, so withholding it would only stop an
        // administrator finding out that the identifier they were given is
        // wrong.
        (await admin.DeleteAsync($"/api/auth/admin/users/{Guid.NewGuid()}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EveryAdministrativeActionIsRecordedAgainstTheAccountItWasAimedAt()
    {
        var admin = _factory.CreateBrowserClient();
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, admin, "audit-admin");

        var administratorId = await AdministrationFlow.IdOfAsync(
            _factory, administrator.EmailAddress);

        var subject = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), "audit-subject");

        var subjectId = await AdministrationFlow.IdOfAsync(_factory, subject.EmailAddress);

        await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{subjectId}/roles",
            new { roles = new[] { "Contributor" } });

        await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{subjectId}/suspension",
            new { suspended = true, reason = "Published a draft that was not theirs to publish." });

        await admin.PutAsJsonAsync(
            $"/api/auth/admin/users/{subjectId}/suspension",
            new { suspended = false, reason = (string?)null });

        var log = await admin.GetAsync($"/api/auth/admin/audit?subjectId={subjectId}");

        log.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entries = (await log.ReadJsonAsync()).GetProperty("actions").EnumerateArray().ToArray();

        entries.Select(entry => entry.GetProperty("action").GetString())
            .ShouldBe(
                ["roles-changed", "account-suspended", "account-reinstated"],
                ignoreOrder: true);

        // Newest first. Asserted as an ordering over the timestamps rather than
        // as a fixed sequence of action names: three requests can land inside
        // one tick of the system clock, and a test that depended on them not
        // doing so would fail on a fast machine roughly never and then once.
        var stamps = entries
            .Select(entry => entry.GetProperty("createdAt").GetDateTimeOffset())
            .ToArray();

        stamps.ShouldBe(stamps.OrderByDescending(stamp => stamp).ToArray());

        foreach (var entry in entries)
        {
            entry.GetProperty("actorUserId").GetGuid().ShouldBe(administratorId);
            entry.GetProperty("subjectUserId").GetGuid().ShouldBe(subjectId);

            // Who, to whom, and when. The three things a record of an
            // administrative action is for.
            entry.GetProperty("createdAt").GetDateTimeOffset()
                .ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(-1));
        }

        var roleChange = entries.Single(
            entry => entry.GetProperty("action").GetString() == "roles-changed");

        // What changed, not merely that something did. A log saying "roles were
        // changed" is one nobody can audit.
        roleChange.GetProperty("rolesBefore").ValueKind.ShouldBe(JsonValueKind.Null);
        roleChange.GetProperty("rolesAfter").EnumerateArray()
            .Select(role => role.GetString())
            .ShouldBe(["Contributor"]);

        var suspension = entries.Single(
            entry => entry.GetProperty("action").GetString() == "account-suspended");

        suspension.GetProperty("reason").GetString()
            .ShouldNotBeNull()
            .ShouldContain("not theirs to publish");
    }

    [Fact]
    public async Task TheRecordOfADeletionOutlivesTheAccountItDeleted()
    {
        // The reason the log copies display names onto the row instead of
        // resolving them the way the flag queue does. The one entry most worth
        // keeping is the one whose subject no longer exists, and an entry that
        // read as a bare identifier would be a record of nothing.
        var admin = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, admin, "audit-delete-admin");

        var doomed = new AccountFlow(
            _factory.CreateBrowserClient(),
            AccountFlow.NewAddress("audit-delete"),
            "Kel Dor Archivist");

        await doomed.EstablishAsync(_factory.Email);

        var doomedId = await AdministrationFlow.IdOfAsync(_factory, doomed.EmailAddress);

        (await admin.SendAsync(new HttpRequestMessage(
                HttpMethod.Delete, $"/api/auth/admin/users/{doomedId}")
            {
                Content = JsonContent.Create(new { reason = "Requested by the account holder." }),
            }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AdministrationFlow.ExistsAsync(_factory, doomedId)).ShouldBeFalse();

        var log = await admin.GetAsync(
            $"/api/auth/admin/audit?subjectId={doomedId}&action=account-deleted");

        log.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entry = (await log.ReadJsonAsync())
            .GetProperty("actions").EnumerateArray().Single();

        entry.GetProperty("subjectUserId").GetGuid().ShouldBe(doomedId);
        entry.GetProperty("subjectDisplayName").GetString().ShouldBe("Kel Dor Archivist");
        entry.GetProperty("reason").GetString()
            .ShouldNotBeNull()
            .ShouldContain("account holder");

        // The address is not in the record. An audit table is the one place on
        // this platform that outlives an account, which makes it exactly the
        // wrong place to keep the address of somebody who asked to be deleted.
        entry.ToString().ShouldNotContain(doomed.EmailAddress);
    }
}
