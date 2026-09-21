using System.Net;
using Microsoft.AspNetCore.Hosting;
using Shouldly;
using Sw5e.Api.Tests.Integration.Accounts;

namespace Sw5e.Api.Tests.Integration.Moderation;

/// <summary>
/// That the two budgets on reporting actually refuse traffic, rather than
/// merely being registered.
/// </summary>
/// <remarks>
/// <para>
/// They are two tests because they are two defences against two different
/// attackers, and each one passes while the other is broken.
/// </para>
/// <para>
/// The per-caller window is keyed on the client address, which is what stops
/// one machine flooding the endpoint and what an attacker with a pool of
/// addresses walks straight past. The per-account quota is checked in the
/// handler against the reporter's own identifier, which is what survives that
/// attacker and what a single machine never reaches. A suite that only proved
/// one of them would be proving the platform is defended against whichever
/// attacker it happened to imagine.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class FlagRateLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheCallerBudgetRefusesOnceItIsSpent()
    {
        await using var factory = new ThrottledFlagApiFactory(postgres, submitRequests: 2);

        await FlagFlow.ClearAsync(factory);

        var client = factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(factory, client, "throttled");

        // Every request names a different document, so nothing here is refused
        // as a duplicate. What is being measured is the budget and only the
        // budget.
        var keys = new[] { "wookiee", "human", "twilek", "bothan", "zabrak" };
        var seen = new List<HttpStatusCode>();

        foreach (var key in keys)
        {
            var response = await FlagFlow.RaiseAsync(
                client, "text-error", "species", key, "A typo.");

            seen.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // A refusal that does not say when to come back converts a
                // flood into a tighter loop rather than shedding it.
                response.Headers.RetryAfter.ShouldNotBeNull();
                break;
            }
        }

        seen.ShouldContain(HttpStatusCode.TooManyRequests);

        // The assertion that makes this more than a status-code check: the
        // refused requests wrote nothing. A limiter that answered 429 after the
        // insert would look identical from outside and would be no defence at
        // all.
        (await FlagFlow.StoredAsync(factory)).Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task TheAccountQuotaRefusesEvenWhenTheCallerBudgetIsUntouched()
    {
        // The caller budget is left wide open here on purpose. This is the
        // defence that has to hold against somebody who has an account and a
        // different address for every request, and the only way to prove it is
        // the one thing the limiter cannot see: who is asking.
        await using var factory = new ThrottledFlagApiFactory(
            postgres, submitRequests: 1000, reportsPerDay: 2);

        await FlagFlow.ClearAsync(factory);

        var client = factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(factory, client, "quota");

        (await FlagFlow.RaiseAsync(client, "text-error", "species", "wookiee", "One."))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await FlagFlow.RaiseAsync(client, "text-error", "species", "human", "Two."))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var refused = await FlagFlow.RaiseAsync(
            client, "text-error", "species", "twilek", "Three.");

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        (await refused.ReadJsonAsync()).GetProperty("code").GetString()
            .ShouldBe("report-quota");

        (await FlagFlow.StoredAsync(factory)).Count.ShouldBe(2);

        // A second account is unaffected, which is what makes this a per-account
        // quota rather than a global one that any single abuser could use to
        // shut everybody else out.
        var other = factory.CreateBrowserClient();
        await FlagFlow.SignInAsync(factory, other, "quota-neighbour");

        (await FlagFlow.RaiseAsync(other, "text-error", "species", "wookiee", "Theirs."))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    /// <summary>
    /// The same host with the reporting budgets turned down far enough that a
    /// test can spend them.
    /// </summary>
    /// <remarks>
    /// Turned down through configuration rather than by substituting the
    /// limiter, so what runs is the limiter the deployment runs. A test against
    /// a stand-in would prove the stand-in refuses traffic.
    /// </remarks>
    private sealed class ThrottledFlagApiFactory(
        PostgresFixture postgres,
        int submitRequests,
        int reportsPerDay = 50) : AccountApiFactory(postgres)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting(
                "Flags:RateLimits:SubmitRequests",
                submitRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));

            builder.UseSetting(
                "Flags:RateLimits:AccountReportsPerDay",
                reportsPerDay.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
