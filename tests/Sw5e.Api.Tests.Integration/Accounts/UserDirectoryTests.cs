using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Sw5e.Identity;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// That the account directory is reachable by an administrator and by nobody
/// else, in any response shape.
/// </summary>
/// <remarks>
/// <para>
/// This is the endpoint that made the rest of the administrative surface
/// usable, and it is also the most dangerous thing on the platform to get
/// wrong. It is a list of real people's email addresses. Every other response
/// the API produces withholds somebody else's address on principle — the flag
/// queue shows Contributors a display name and never an address — and this one
/// does not, because "somebody wrote to me asking to contribute, find them" is
/// a task with no other answer.
/// </para>
/// <para>
/// So the tests below are mostly refusals, and each refusal is paired with the
/// grant it is the opposite of. A negative on its own proves nothing: an
/// endpoint that refused everybody would pass every one of them, and would be
/// just as broken as one that refused nobody.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class UserDirectoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AccountApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new AccountApiFactory(postgres);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /* -------------------------------------------------------------- refusals */

    [Fact]
    public async Task AnAnonymousCallerCannotListUsers()
    {
        var client = _factory.CreateBrowserClient();

        var response = await client.GetAsync("/api/auth/admin/users");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The body has to be checked as well as the status. A 401 whose body
        // happened to carry the page it was refusing would be a leak with a
        // reassuring status line on top.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("@sw5e.test");
    }

    [Fact]
    public async Task ACommunityAccountCannotListUsers()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.MemberAsync(_factory, client, "directory-community");

        var response = await client.GetAsync("/api/auth/admin/users");

        // Forbidden rather than unauthorized: the caller is known and is not
        // permitted. Telling them to sign in again would be advice they have
        // already followed.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("@sw5e.test");
    }

    [Fact]
    public async Task AContributorCannotListUsers()
    {
        var client = _factory.CreateBrowserClient();

        await AdministrationFlow.SignInWithRoleAsync(
            _factory, client, "directory-contributor", Sw5eRoles.Contributor);

        // The Contributor session is real and can reach the contributor tools;
        // the queue answers it. That is what makes the refusal below about this
        // endpoint rather than about the session.
        (await client.GetAsync("/api/flags")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/auth/admin/users");

        // Publishing content and reading the address of everybody who has ever
        // registered are different privileges, and the Contributor role is only
        // the first.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("@sw5e.test");
    }

    [Fact]
    public async Task AnAdministratorWhoSignedInWithACodeCannotListUsers()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, client, "directory-weak");

        await client.PostAsync("/api/auth/logout", content: null);

        // In through the weak door: a code emailed to the address. The account
        // is an administrator and has a passkey; it simply did not use it.
        var weak = _factory.CreateBrowserClient();
        await AdministrationFlow.SignInWithEmailedCodeAsync(
            _factory, weak, administrator.EmailAddress);

        // The session is real — the account area answers it — which is the
        // whole reason the weaker route exists.
        (await weak.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await weak.GetAsync("/api/auth/admin/users");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Machine-readable, so the browser application can say "sign in with
        // your passkey" rather than "you do not have access", which would be
        // both wrong and unactionable.
        (await response.ReadJsonAsync())
            .GetProperty("code").GetString()
            .ShouldBe(Sw5eIdentityServiceCollectionExtensions.StrongAuthenticationRequired);

        (await response.Content.ReadAsStringAsync()).ShouldNotContain("@sw5e.test");
    }

    [Fact]
    public async Task EveryAdministrativeRouteIsClosedToACommunityAccount()
    {
        // The directory is the obvious one, and it would be a poor feature that
        // secured the obvious one. Every route added alongside it is checked
        // from the same session in the same test, so a route added later that
        // forgets its policy fails here rather than in production.
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.MemberAsync(_factory, client, "directory-sweep");

        var target = await AdministrationFlow.IdOfAsync(
            _factory,
            (await AdministrationFlow.MemberAsync(
                _factory, _factory.CreateBrowserClient(), "directory-sweep-target")).EmailAddress);

        var refusals = new List<(string Route, HttpResponseMessage Response)>
        {
            ("list", await client.GetAsync("/api/auth/admin/users")),
            ("detail", await client.GetAsync($"/api/auth/admin/users/{target}")),
            ("audit", await client.GetAsync("/api/auth/admin/audit")),
            ("suspension", await client.PutAsJsonAsync(
                $"/api/auth/admin/users/{target}/suspension",
                new { suspended = true, reason = "no" })),
            ("roles", await client.PutAsJsonAsync(
                $"/api/auth/admin/users/{target}/roles",
                new { roles = new[] { Sw5eRoles.Administrator } })),
            ("delete", await client.DeleteAsync($"/api/auth/admin/users/{target}")),
        };

        foreach (var (route, response) in refusals)
        {
            response.StatusCode.ShouldBe(
                HttpStatusCode.Forbidden,
                $"the {route} route must be closed to a community account");
        }

        // And every refusal was real rather than cosmetic. A 403 from an
        // endpoint that had already suspended, promoted or deleted the account
        // would be worse than no check at all.
        (await AdministrationFlow.ExistsAsync(_factory, target)).ShouldBeTrue();
        (await AdministrationFlow.IsSuspendedAsync(_factory, target)).ShouldBeFalse();
    }

    [Fact]
    public async Task ARefusalSaysNothingAboutWhetherTheAccountExists()
    {
        // The property that keeps this from being an enumeration oracle. A
        // caller who is not an administrator must not be able to tell a real
        // account identifier from one they invented, and the mechanism is that
        // authorization refuses before any handler runs — so there is no query,
        // no branch and nothing for the two answers to differ on.
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.MemberAsync(_factory, client, "directory-oracle");

        var real = await AdministrationFlow.IdOfAsync(
            _factory,
            (await AdministrationFlow.MemberAsync(
                _factory, _factory.CreateBrowserClient(), "directory-oracle-target")).EmailAddress);

        var invented = Guid.NewGuid();

        var known = await client.GetAsync($"/api/auth/admin/users/{real}");
        var unknown = await client.GetAsync($"/api/auth/admin/users/{invented}");

        known.StatusCode.ShouldBe(unknown.StatusCode);

        // Byte for byte, once the per-request trace identifier is out of the
        // way. Comparing the whole body would fail on a value that differs by
        // design and says nothing about any account.
        Scrub(await known.Content.ReadAsStringAsync())
            .ShouldBe(Scrub(await unknown.Content.ReadAsStringAsync()));

        // The same for an anonymous caller, who is the party most likely to be
        // probing.
        var anonymous = _factory.CreateBrowserClient();

        var anonymousKnown = await anonymous.GetAsync($"/api/auth/admin/users/{real}");
        var anonymousUnknown = await anonymous.GetAsync($"/api/auth/admin/users/{invented}");

        anonymousKnown.StatusCode.ShouldBe(anonymousUnknown.StatusCode);

        Scrub(await anonymousKnown.Content.ReadAsStringAsync())
            .ShouldBe(Scrub(await anonymousUnknown.Content.ReadAsStringAsync()));
    }

    /* -------------------------------------------------------------- the grant */

    [Fact]
    public async Task AnAdministratorCanFindAnAccountByItsAddressAndThenActOnIt()
    {
        // The whole point of the feature, in one test. Before this endpoint
        // existed the role grant was addressed by an identifier nothing in the
        // API would disclose, so the platform's one administrative capability
        // could only be exercised from a database client.
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "directory-admin");

        var target = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), "directory-found");

        var found = await client.GetAsync(
            $"/api/auth/admin/users?q={Uri.EscapeDataString(target.EmailAddress)}");

        found.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await found.ReadJsonAsync();

        body.GetProperty("totalCount").GetInt32().ShouldBe(1);

        var user = body.GetProperty("users").EnumerateArray().Single();

        user.GetProperty("email").GetString().ShouldBe(target.EmailAddress);
        user.GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        user.GetProperty("secondFactorEnrolled").GetBoolean().ShouldBeTrue();
        user.GetProperty("suspension").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // And the identifier it just handed back is the one the role endpoint
        // takes. That is the join this feature exists to make: without it the
        // two halves are a URL nobody can build.
        var id = user.GetProperty("id").GetGuid();

        var granted = await client.PutAsJsonAsync(
            $"/api/auth/admin/users/{id}/roles",
            new { roles = new[] { Sw5eRoles.Contributor } });

        granted.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await granted.ReadJsonAsync())
            .GetProperty("roles").EnumerateArray().Select(role => role.GetString())
            .ShouldContain(Sw5eRoles.Contributor);
    }

    [Fact]
    public async Task TheDirectoryFindsAnAccountByPartOfItsAddress()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "directory-search");

        var target = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), "directory-searchable");

        // The local part alone, which is how somebody types an address they
        // half remember.
        var localPart = target.EmailAddress.Split('@')[0];

        var byFragment = await client.GetAsync(
            $"/api/auth/admin/users?q={Uri.EscapeDataString(localPart)}");

        byFragment.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await byFragment.ReadJsonAsync()).GetProperty("totalCount").GetInt32().ShouldBe(1);

        // Case does not matter, because a person typing an address into a
        // search box does not know that it should.
        var shouted = await client.GetAsync(
            $"/api/auth/admin/users?q={Uri.EscapeDataString(localPart.ToUpperInvariant())}");

        shouted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await shouted.ReadJsonAsync()).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task TheDirectoryFindsAnAccountByDisplayName()
    {
        // An address is not always what an administrator has. Somebody
        // reporting a problem in a forum thread is a display name, and being
        // unable to search for one would mean the directory only answers a
        // question the administrator could already answer.
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "directory-byname");

        var named = new AccountFlow(
            _factory.CreateBrowserClient(),
            AccountFlow.NewAddress("directory-byname-target"),
            "Ithorian Quartermaster");

        await named.EstablishAsync(_factory.Email);

        var response = await client.GetAsync(
            "/api/auth/admin/users?q=" + Uri.EscapeDataString("ithorian quarter"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var addresses = (await response.ReadJsonAsync())
            .GetProperty("users").EnumerateArray()
            .Select(user => user.GetProperty("email").GetString())
            .ToArray();

        addresses.ShouldContain(named.EmailAddress);
    }

    [Fact]
    public async Task TheDirectoryRefusesASearchTooShortToBeOne()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "directory-short");

        var response = await client.GetAsync("/api/auth/admin/users?q=a");

        // A one-character term matches most of the table, which makes "search"
        // an expensive synonym for "everything" and produces a page nobody can
        // read. A caller who wants everything omits the parameter.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnUnrecognisedFilterIsRefusedRatherThanIgnored()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "directory-filter");

        // Silently dropping a filter shows an administrator the whole directory
        // while they believe they are looking at one slice of it, which on a
        // page whose next action is "suspend" means acting on the wrong person.
        (await client.GetAsync("/api/auth/admin/users?role=Overlord"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.GetAsync("/api/auth/admin/users?status=sideways"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The control: the same filters with values that exist are answered.
        (await client.GetAsync($"/api/auth/admin/users?role={Sw5eRoles.Administrator}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.GetAsync("/api/auth/admin/users?status=suspended"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheRoleFilterReturnsTheAccountsThatHoldTheRole()
    {
        var client = _factory.CreateBrowserClient();
        var administrator = await AdministrationFlow.AdministratorAsync(
            _factory, client, "directory-role-filter");

        var member = await AdministrationFlow.MemberAsync(
            _factory, _factory.CreateBrowserClient(), "directory-role-filter-member");

        var administrators = await client.GetAsync(
            $"/api/auth/admin/users?role={Sw5eRoles.Administrator}&pageSize=100");

        administrators.StatusCode.ShouldBe(HttpStatusCode.OK);

        var addresses = (await administrators.ReadJsonAsync())
            .GetProperty("users").EnumerateArray()
            .Select(user => user.GetProperty("email").GetString())
            .ToArray();

        addresses.ShouldContain(administrator.EmailAddress);

        // The half that makes the assertion above mean something: a filter that
        // returned everybody would satisfy it.
        addresses.ShouldNotContain(member.EmailAddress);
    }

    [Fact]
    public async Task ThePageSizeIsClampedRatherThanObeyed()
    {
        var client = _factory.CreateBrowserClient();
        await AdministrationFlow.AdministratorAsync(_factory, client, "directory-paging");

        var response = await client.GetAsync("/api/auth/admin/users?pageSize=100000");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The page size arrives in a query string, so without a ceiling one
        // request asking for a million rows is a cheap way to make the server
        // do a great deal of work.
        (await response.ReadJsonAsync()).GetProperty("pageSize").GetInt32().ShouldBe(100);
    }

    /// <summary>
    /// Removes the values that differ between two responses by design.
    /// </summary>
    /// <remarks>
    /// The problem details middleware stamps a per-request trace identifier
    /// onto every refusal, so two refusals are never byte-identical and
    /// comparing them raw would fail on a value that says nothing about any
    /// account.
    /// </remarks>
    private static string Scrub(string body) =>
        System.Text.RegularExpressions.Regex.Replace(
            body,
            "\"traceId\"\\s*:\\s*\"[^\"]*\"",
            "\"traceId\":\"*\"");
}
