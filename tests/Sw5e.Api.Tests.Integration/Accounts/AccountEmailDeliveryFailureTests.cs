using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Sw5e.Email;
using Sw5e.Email.Accounts;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// What the account endpoints do when the mail provider refuses everything.
/// </summary>
/// <remarks>
/// <para>
/// The failure this suite exists for was observed against a relay that answered
/// <c>554 5.9.2 Sender domain is not valid</c>: the adapter turned the returned
/// failure into an exception, and registration and sign-in both answered 500.
/// That is a broken account system, and it is also an enumeration hazard.
/// <c>register</c> and <c>email/code</c> promise one answer whether or not the
/// address has an account, and only the fact that both branches happen to send
/// a message kept the 500 from being an oracle — the day the unknown-address
/// send was dropped as an optimisation, registered addresses would have errored
/// and unknown ones would have succeeded.
/// </para>
/// <para>
/// So these tests run the real adapter and break the seam underneath it.
/// Substituting <c>IAccountEmailSender</c>, the way the rest of the suite does,
/// would replace the very code that decides what a failure means.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class AccountEmailDeliveryFailureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task APermanentDeliveryFailureStillAnswersRegistrationWithTheStandardAccepted()
    {
        await using var factory = BrokenMailApiFactory.Permanent(postgres);
        var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = AccountFlow.NewAddress("permanent"), displayName = "Nobody" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = await response.ReadJsonAsync();
        body.GetProperty("status").GetString().ShouldBe("pending");
        body.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();

        // The send was attempted and refused. Without this the test would also
        // pass against an implementation that answered 202 by never trying.
        factory.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ATransientDeliveryFailureStillAnswersRegistrationWithTheStandardAccepted()
    {
        await using var factory = BrokenMailApiFactory.Transient(postgres);
        var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = AccountFlow.NewAddress("transient"), displayName = "Nobody" });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("pending");
        factory.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task APermanentDeliveryFailureStillAnswersASignInCodeRequestWithTheStandardAccepted()
    {
        await using var factory = BrokenMailApiFactory.Permanent(postgres);
        var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/email/code", new { email = AccountFlow.NewAddress("code-permanent") });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("pending");
        factory.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task ATransientDeliveryFailureStillAnswersASignInCodeRequestWithTheStandardAccepted()
    {
        await using var factory = BrokenMailApiFactory.Transient(postgres);
        var client = factory.CreateBrowserClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/email/code", new { email = AccountFlow.NewAddress("code-transient") });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("pending");
        factory.Attempts.ShouldBe(1);
    }

    /// <summary>
    /// The property that was only accidentally true: with the relay refusing
    /// everything, a registered address and an unknown one are still
    /// indistinguishable.
    /// </summary>
    /// <remarks>
    /// Both endpoints, and whole responses rather than a spot-checked field,
    /// because enumeration leaks through whatever differs. The registered
    /// address is established against a working fixture first — the two share
    /// the one database, so the account is real as far as the broken one is
    /// concerned.
    /// </remarks>
    [Fact]
    public async Task ABrokenRelayCannotBeUsedToTellARegisteredAddressFromAnUnknownOne()
    {
        string registered;

        await using (var working = new AccountApiFactory(postgres))
        {
            var account = AccountFlow.For(working.CreateBrowserClient(), "outage-known");
            await account.EstablishAsync(working.Email);
            registered = account.EmailAddress;
        }

        await using var factory = BrokenMailApiFactory.Permanent(postgres);
        var client = factory.CreateBrowserClient();

        foreach (var route in new[] { "/api/auth/register", "/api/auth/email/code" })
        {
            // A fresh unknown address per route: registering one would give the
            // second route an account to find, which is not the branch under
            // test here.
            var unknown = AccountFlow.NewAddress("outage-unknown");

            var known = await client.PostAsJsonAsync(
                route, new { email = registered, displayName = "Somebody Else" });

            var stranger = await client.PostAsJsonAsync(
                route, new { email = unknown, displayName = "Somebody Else" });

            known.StatusCode.ShouldBe(HttpStatusCode.Accepted, route);
            stranger.StatusCode.ShouldBe(known.StatusCode, route);

            (await known.Content.ReadAsStringAsync())
                .ShouldBe(await stranger.Content.ReadAsStringAsync(), route);
        }

        // Four requests, four refused messages: the two branches still do the
        // same amount of work, which is the other half of the property.
        factory.Attempts.ShouldBe(4);
    }

    /// <summary>
    /// The failure is not swallowed: it reaches the readiness surface, and it
    /// does not take the instance out of rotation.
    /// </summary>
    [Fact]
    public async Task AFailedSendIsReportedAsDegradedWithoutFailingReadiness()
    {
        await using var factory = BrokenMailApiFactory.Permanent(postgres);
        var client = factory.CreateBrowserClient();

        var before = await (await client.GetAsync("/api/health/ready")).ReadJsonAsync();
        before.GetProperty("status").GetString().ShouldBe("healthy");

        var address = AccountFlow.NewAddress("degraded");

        await client.PostAsJsonAsync("/api/auth/email/code", new { email = address });

        var response = await client.GetAsync("/api/health/ready");

        // 200, not 503. Every replica sends through the same relay, so draining
        // them cannot route around a mail outage — it only removes capacity
        // from a site whose reading and browsing are entirely unaffected.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var report = await response.ReadJsonAsync();
        report.GetProperty("status").GetString().ShouldBe("degraded");

        var check = report.GetProperty("checks")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == "account-email");

        check.GetProperty("status").GetString().ShouldBe("degraded");

        var description = check.GetProperty("description").GetString();
        description.ShouldNotBeNullOrWhiteSpace();

        // This surface is anonymous. The provider's reply can quote the
        // envelope, so it belongs in the application log and nowhere a stranger
        // can read it back.
        description!.Contains(address, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        description.Contains(BrokenMailApiFactory.ProviderReply, StringComparison.Ordinal)
                   .ShouldBeFalse();
    }

    /// <summary>
    /// The failure also reaches the surface the site itself reads, so the
    /// interface can stop telling people a message is on its way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>/health/ready</c> is for operators and is not what a browser consults;
    /// without this the site keeps promising mail it has just been told was
    /// refused, and then sends the reader to a spam folder to look for it. The
    /// ordering matters and is asserted rather than assumed: the flag reads true
    /// before the registration and false after it, because the account endpoints
    /// attempt their send before they answer. A client asking after its own 202
    /// therefore sees the failure its own request caused, with no polling
    /// interval to be unlucky in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFailedSendIsPublishedOnTheSurfaceTheSiteReads()
    {
        await using var factory = BrokenMailApiFactory.Permanent(postgres);
        var client = factory.CreateBrowserClient();

        var before = await (await client.GetAsync(SiteEnvironment)).ReadJsonAsync();
        before.GetProperty("accountEmailDelivering").GetBoolean().ShouldBeTrue(
            "nothing has been refused yet, so the site has no reason to change what it says");

        await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = AccountFlow.NewAddress("published"), displayName = "Nobody" });

        var after = await (await client.GetAsync(SiteEnvironment)).ReadJsonAsync();
        after.GetProperty("accountEmailDelivering").GetBoolean().ShouldBeFalse(
            "the relay refused the verification message before the 202 was written, so a " +
            "client asking now must be told that mail is not getting out");

        // The rest of the document is untouched. A mail outage is not a
        // statement about which deployment this is, and the QA banner must not
        // start or stop appearing because a relay went down.
        after.GetProperty("isProduction").GetBoolean()
             .ShouldBe(before.GetProperty("isProduction").GetBoolean());
        after.Text("name").ShouldBe(before.Text("name"));
    }

    /// <summary>
    /// What the anonymous surface is allowed to say about a failure: that there
    /// was one, and nothing else.
    /// </summary>
    /// <remarks>
    /// The provider's reply is the named hazard — the relay wrote it about one
    /// envelope and it can quote the recipient — so both it and the address are
    /// ruled out of the whole body rather than out of one field. The assertion
    /// is on the raw text for that reason: a leak into a field nobody thought to
    /// read is still a leak.
    /// </remarks>
    [Fact]
    public async Task TheSurfaceTheSiteReadsNamesNeitherTheAddressNorTheProvidersReply()
    {
        await using var factory = BrokenMailApiFactory.Permanent(postgres);
        var client = factory.CreateBrowserClient();

        var address = AccountFlow.NewAddress("no-leak");

        await client.PostAsJsonAsync("/api/auth/email/code", new { email = address });

        var body = await (await client.GetAsync(SiteEnvironment)).Content.ReadAsStringAsync();

        body.Contains("accountEmailDelivering", StringComparison.Ordinal).ShouldBeTrue(
            "the assertions below are worthless against a body that does not mention " +
            "delivery at all");

        body.Contains(address, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            "an anonymous caller who can see an address in here can enumerate accounts " +
            "by watching which ones appear");
        body.Contains(BrokenMailApiFactory.ProviderReply, StringComparison.Ordinal)
            .ShouldBeFalse(
                "the relay wrote that sentence about a specific envelope and it can quote " +
                "the recipient");

        // The pieces of the reply, not only the whole of it. A future adapter
        // that forwarded a fragment — the status code and the phrase — would
        // satisfy a whole-string check and still be publishing what a relay
        // said about one message.
        body.Contains("554", StringComparison.Ordinal).ShouldBeFalse();
        body.Contains("Sender domain", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    /// <summary>
    /// The new surface is not a new oracle: it answers the same to a caller who
    /// has just probed a registered address and to one who has just probed a
    /// stranger, byte for byte, in both delivery states.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the question the change had to answer before it could ship.
    /// Publishing a delivery state is safe only because the state is global; a
    /// per-address one would rebuild exactly the oracle the 202 exists to
    /// prevent, since asking whether mail to an address failed is asking whether
    /// that address has an account.
    /// </para>
    /// <para>
    /// So the test probes an account endpoint with a known address and with a
    /// stranger, reads the site surface after each, and compares whole bodies
    /// rather than the one field — a difference anywhere is an enumeration
    /// channel, and the point is that nothing in here varies with who was asked
    /// about. Both delivery states are covered, and the working one is not
    /// ceremony: with the relay refusing everything, a per-address
    /// implementation has something to differ about, and with it working, one
    /// does not. Only the pair rules the design out.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDeliveryFlagCannotBeUsedToTellARegisteredAddressFromAnUnknownOne()
    {
        string registered;

        await using (var working = new AccountApiFactory(postgres))
        {
            var account = AccountFlow.For(working.CreateBrowserClient(), "flag-known");
            await account.EstablishAsync(working.Email);
            registered = account.EmailAddress;

            // Healthy relay first. Both probes must leave the same answer
            // behind, and it must be the answer that changes no wording.
            var healthy = working.CreateBrowserClient();

            await healthy.PostAsJsonAsync("/api/auth/email/code", new { email = registered });
            var afterKnown = await (await healthy.GetAsync(SiteEnvironment))
                .Content.ReadAsStringAsync();

            await healthy.PostAsJsonAsync(
                "/api/auth/email/code", new { email = AccountFlow.NewAddress("flag-unknown") });
            var afterStranger = await (await healthy.GetAsync(SiteEnvironment))
                .Content.ReadAsStringAsync();

            afterKnown.ShouldBe(afterStranger);
            afterKnown.ShouldContain(
                "\"accountEmailDelivering\":true",
                customMessage: "a working relay must leave the site saying what it always said");
        }

        // And with the relay refusing everything, where a per-address
        // implementation would finally have something to differ about.
        await using var broken = BrokenMailApiFactory.Permanent(postgres);
        var client = broken.CreateBrowserClient();

        await client.PostAsJsonAsync("/api/auth/register",
            new { email = registered, displayName = "Somebody Else" });
        var brokenKnown = await (await client.GetAsync(SiteEnvironment))
            .Content.ReadAsStringAsync();

        await client.PostAsJsonAsync("/api/auth/register",
            new { email = AccountFlow.NewAddress("flag-unknown"), displayName = "Somebody Else" });
        var brokenStranger = await (await client.GetAsync(SiteEnvironment))
            .Content.ReadAsStringAsync();

        brokenKnown.ShouldBe(brokenStranger);
        brokenKnown.ShouldContain(
            "\"accountEmailDelivering\":false",
            customMessage:
                "and the outage must actually be visible, or the equality above is the " +
                "trivial one between two healthy answers");
    }

    /// <summary>Where the site reads the delivery state from.</summary>
    private const string SiteEnvironment = "/api/site/environment";

    /// <summary>
    /// Hosts the API with the real mail adapter in place and the provider seam
    /// underneath it refusing every message.
    /// </summary>
    /// <remarks>
    /// Both halves of that seam are replaced, because the adapter uses both:
    /// verification goes through <see cref="IAccountEmailService"/>, which owns
    /// that message's wording, and the passkey, sign-in-code and notice
    /// messages are composed by the adapter and handed to
    /// <see cref="IEmailSender"/> directly.
    /// </remarks>
    private sealed class BrokenMailApiFactory : AccountApiFactory
    {
        /// <summary>What the relay is pretending to have said.</summary>
        public const string ProviderReply =
            "The SMTP relay returned 554 (TransactionFailed). 5.9.2 Sender domain is not valid.";

        private readonly Func<EmailDeliveryResult> _outcome;
        private int _attempts;

        private BrokenMailApiFactory(PostgresFixture postgres, Func<EmailDeliveryResult> outcome)
            : base(postgres) => _outcome = outcome;

        /// <summary>How many messages the provider has been handed and refused.</summary>
        public int Attempts => Volatile.Read(ref _attempts);

        protected override bool RecordEmailInsteadOfSending => false;

        public static BrokenMailApiFactory Permanent(PostgresFixture postgres) =>
            new(postgres, () => EmailDeliveryResult.Permanent(ProviderReply));

        public static BrokenMailApiFactory Transient(PostgresFixture postgres) =>
            new(postgres, () => EmailDeliveryResult.Transient(
                "The SMTP relay timed out after four attempts."));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // A sending identity has to exist for the adapter to compose a
            // message at all; which provider it names is irrelevant, because
            // both halves of the seam are replaced below.
            builder.UseSetting("Email:Provider", "Capture");
            builder.UseSetting("Email:FromAddress", "noreply@sw5e.test");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new RefusingEmailSender(Refuse));

                services.RemoveAll<IAccountEmailService>();
                services.AddSingleton<IAccountEmailService>(new RefusingAccountEmailService(Refuse));
            });
        }

        private EmailDeliveryResult Refuse()
        {
            Interlocked.Increment(ref _attempts);
            return _outcome();
        }
    }

    private sealed class RefusingEmailSender(Func<EmailDeliveryResult> refuse) : IEmailSender
    {
        public Task<EmailDeliveryResult> SendAsync(
            EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(refuse());
    }

    private sealed class RefusingAccountEmailService(Func<EmailDeliveryResult> refuse)
        : IAccountEmailService
    {
        public Task<EmailDeliveryResult> SendEmailVerificationAsync(
            EmailAddress recipient,
            string verificationUrl,
            TimeSpan? validFor = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(refuse());

        public Task<EmailDeliveryResult> SendPasswordResetAsync(
            EmailAddress recipient,
            string resetUrl,
            TimeSpan? validFor = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(refuse());
    }
}
