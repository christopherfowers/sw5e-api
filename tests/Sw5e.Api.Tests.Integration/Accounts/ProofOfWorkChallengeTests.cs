using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Shouldly;

namespace Sw5e.Api.Tests.Integration.Accounts;

/// <summary>
/// The proof-of-work challenge in front of registration and the emailed
/// sign-in code.
/// </summary>
/// <remarks>
/// <para>
/// Every test here solves the challenge the way a browser would: it fetches one
/// from <c>GET /api/auth/challenge</c>, hashes until it finds a counter, and
/// sends the whole thing back in headers. Nothing reaches into the container to
/// mint a challenge or to ask the verifier a question directly, for the same
/// reason the rest of this suite refuses to fabricate a session — a test that
/// signs its own challenges is asserting that the server accepts what the
/// server made, which is true of a server that accepts anything.
/// </para>
/// <para>
/// The one thing the tests deliberately do <em>not</em> share with production
/// is the leading-zero-bit count. <see cref="LeadingZeroBits"/> below is written
/// independently, and differently, precisely so that a bug in the server's
/// version — counting nibbles, ignoring the byte that straddles the boundary —
/// shows up as a disagreement rather than being reproduced identically on both
/// sides and cancelling out.
/// </para>
/// </remarks>
[Collection(AccountTestCollection.Name)]
public sealed class ProofOfWorkChallengeTests(PostgresFixture postgres)
{
    /// <summary>
    /// The signing key the challenged hosts below share.
    /// </summary>
    /// <remarks>
    /// Not a secret and not treated as one: it signs challenges for a host that
    /// exists for the length of one test and is thrown away. It is shared
    /// between two hosts on purpose in the tests that need one instance to
    /// accept a challenge another instance issued — which is also the real
    /// arrangement behind a load balancer.
    /// </remarks>
    private const string Secret = "test-only-proof-of-work-signing-key-0123456789";

    private const string SaltHeader = "X-Sw5e-Challenge-Salt";
    private const string DifficultyHeader = "X-Sw5e-Challenge-Difficulty";
    private const string ExpiresHeader = "X-Sw5e-Challenge-Expires";
    private const string SignatureHeader = "X-Sw5e-Challenge-Signature";
    private const string CounterHeader = "X-Sw5e-Challenge-Counter";

    /// <summary>
    /// Low enough that the suite is not spending seconds per test on hashing,
    /// and high enough to be above the configured floor of eight bits — which
    /// means a wrong counter is genuinely unlikely to pass by luck.
    /// </summary>
    private const int Difficulty = 12;

    // ── The gate open ────────────────────────────────────────────────────────

    [Fact]
    public async Task ASolvedChallengeAdmitsARegistration()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var address = AccountFlow.NewAddress("pow-solved");

        var response = await RegisterAsync(client, address, challenge, Solve(challenge));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The status alone would pass against a filter that refused the request
        // and a handler that never ran, if the refusal happened to be a 202. The
        // message is the evidence that the registration actually happened.
        factory.Email.CountOf(AccountMessageKind.Verification, address).ShouldBe(1);
    }

    [Fact]
    public async Task ASolvedChallengeAdmitsASignInCodeRequest()
    {
        // Worth its own case rather than trusting the register test to cover
        // the filter. This is the endpoint that ends in a message sent to an
        // address the caller chose, and an attachment that was forgotten here
        // would be invisible in the other test.
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var address = AccountFlow.NewAddress("pow-code");

        var response = await PostAsync(
            client, "/api/auth/email/code", new { email = address }, challenge, Solve(challenge));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Email.For(address).ShouldNotBeEmpty();
    }

    // ── The gate closed ──────────────────────────────────────────────────────

    [Fact]
    public async Task ARequestWithNoChallengeAtAllIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var address = AccountFlow.NewAddress("pow-none");

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", new { email = address, displayName = "Nobody" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Distinguishable from the limiter's 429, because the two ask the
        // client for opposite things: back off, versus do the work and retry
        // now.
        (await response.ReadJsonAsync()).GetProperty("code").GetString()
            .ShouldBe("challenge-required");

        // Refused before the handler, not after. A 403 issued once the account
        // had been written and the mail was on its way would be theatre.
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASignInCodeRequestWithNoChallengeIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var address = AccountFlow.NewAddress("pow-code-none");

        var response = await client.PostAsJsonAsync("/api/auth/email/code", new { email = address });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AWrongCounterIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var address = AccountFlow.NewAddress("pow-wrong-counter");

        // A counter chosen because it demonstrably does not produce the
        // required zeros, rather than "the right answer plus one" — which is
        // itself a valid solution once every few thousand challenges and would
        // make this test flake.
        var wrong = UnsolvedCounter(challenge);

        var response = await RegisterAsync(client, address, challenge, wrong);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASolutionThatOnlyReachesALowerDifficultyIsRefused()
    {
        // The near-miss, and the case a hash check that counts hex characters
        // instead of bits gets wrong. Eight zero bits is two zero hex
        // characters; twelve is three. A solution with eight bits and no more
        // rounds down to the same number of whole zero bytes as the real
        // requirement, so a verifier that ignores the byte straddling the
        // boundary accepts it.
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var address = AccountFlow.NewAddress("pow-near-miss");

        var nearMiss = NearMissCounter(challenge, atLeast: 8);

        var response = await RegisterAsync(client, address, challenge, nearMiss);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    // ── Tampering ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEditedSaltIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var issued = await FetchAsync(client);

        // A salt the caller chose, solved properly for the advertised
        // difficulty, carrying the signature the server really issued. Only the
        // signature covering the salt stops this: without it, an attacker
        // fetches one challenge and then mints an unlimited supply of
        // single-use salts from it.
        var forged = issued with { Salt = new string('a', 32) };
        var address = AccountFlow.NewAddress("pow-salt");

        var response = await RegisterAsync(client, address, forged, Solve(forged));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEditedDifficultyIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var issued = await FetchAsync(client);

        // The most valuable edit available to an attacker: talk the difficulty
        // down to something free, then solve that. The counter is a genuine
        // solution for the claimed difficulty, so nothing but the signature can
        // catch it.
        var forged = issued with { Difficulty = 1 };
        var address = AccountFlow.NewAddress("pow-difficulty");

        var response = await RegisterAsync(client, address, forged, Solve(forged));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEditedExpiryIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var issued = await FetchAsync(client);
        var counter = Solve(issued);

        // Pushed a year out, which is what somebody who wanted one challenge to
        // last forever would do. The expiry is inside the signature, so this is
        // caught as a forgery rather than by the freshness check — and it is
        // the reason the expiry has to be inside the signature at all.
        var forged = issued with
        {
            ExpiresAt = DateTimeOffset.Parse(issued.ExpiresAt, CultureInfo.InvariantCulture)
                .AddYears(1)
                .ToString("O", CultureInfo.InvariantCulture),
        };

        var address = AccountFlow.NewAddress("pow-expiry");

        var response = await RegisterAsync(client, address, forged, counter);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEditedSignatureIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var issued = await FetchAsync(client);
        var counter = Solve(issued);

        // One character, at the end, where a comparison that stops at the first
        // difference would take longest to notice. Everything else about the
        // challenge is genuine.
        var forged = issued with
        {
            Signature = issued.Signature[..^1] + (issued.Signature[^1] == 'a' ? 'b' : 'a'),
        };

        var address = AccountFlow.NewAddress("pow-signature");

        var response = await RegisterAsync(client, address, forged, counter);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    // ── Freshness and single use ─────────────────────────────────────────────

    [Fact]
    public async Task AnExpiredChallengeIsRefused()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var counter = Solve(challenge);

        // Past the ten-minute lifetime. Solved correctly, signed correctly,
        // never used — and worthless, because the expiry is what bounds how
        // long a challenge harvested in bulk stays spendable.
        factory.Clock.Advance(TimeSpan.FromMinutes(11));

        var address = AccountFlow.NewAddress("pow-expired");

        var response = await RegisterAsync(client, address, challenge, counter);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AChallengeIssuedWithALongerLifetimeIsRefusedOnceItIsShortened()
    {
        // The other half of the freshness check, and the one nothing else
        // reaches. A challenge signed by an instance configured with an hour's
        // lifetime carries a perfectly valid signature; the instance that has
        // since been told to use ten minutes must still refuse it, or lowering
        // the lifetime does nothing until every challenge issued under the old
        // one has run out.
        await using var generous = new ChallengedApiFactory(postgres, lifetime: TimeSpan.FromHours(1));
        await using var strict = new ChallengedApiFactory(postgres);

        var challenge = await FetchAsync(generous.CreateBrowserClient());
        var address = AccountFlow.NewAddress("pow-lifetime");

        var response = await RegisterAsync(
            strict.CreateBrowserClient(), address, challenge, Solve(challenge));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        strict.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AChallengeIssuedAtALowerDifficultyIsRefusedOnceItIsRaised()
    {
        // Why the verifier pins the difficulty to what is configured now rather
        // than trusting the signed value. An operator raising the difficulty
        // during an attack has to see an effect immediately; otherwise the
        // attacker simply spends the stockpile of cheap challenges they were
        // legitimately issued minutes earlier, and every one of them verifies.
        await using var cheap = new ChallengedApiFactory(postgres, difficulty: 8);
        await using var raised = new ChallengedApiFactory(postgres, difficulty: 16);

        var challenge = await FetchAsync(cheap.CreateBrowserClient());
        challenge.Difficulty.ShouldBe(8);

        var address = AccountFlow.NewAddress("pow-raised");

        var response = await RegisterAsync(
            raised.CreateBrowserClient(), address, challenge, Solve(challenge));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        raised.Email.For(address).ShouldBeEmpty();
    }

    [Fact]
    public async Task AChallengeCannotBeSpentTwice()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var counter = Solve(challenge);

        var first = AccountFlow.NewAddress("pow-replay-first");
        var second = AccountFlow.NewAddress("pow-replay-second");

        (await RegisterAsync(client, first, challenge, counter))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The same salt, the same counter, the same signature, still inside its
        // lifetime. Without single use the whole mechanism is one payment for
        // unlimited registrations, which is exactly the mass-signup case it is
        // here to make expensive.
        var replay = await RegisterAsync(client, second, challenge, counter);

        replay.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.Email.For(second).ShouldBeEmpty();
        factory.Email.CountOf(AccountMessageKind.Verification, first).ShouldBe(1);
    }

    [Fact]
    public async Task AFailedAttemptDoesNotSpendTheChallenge()
    {
        // The order of the checks, asserted. Spending the salt before the work
        // has been verified would let anybody who intercepted a challenge burn
        // it by posting a deliberately wrong counter, turning a defence against
        // strangers into a way to deny service to the person who paid for it.
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var challenge = await FetchAsync(client);
        var address = AccountFlow.NewAddress("pow-not-spent");

        (await RegisterAsync(client, address, challenge, UnsolvedCounter(challenge)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await RegisterAsync(client, address, challenge, Solve(challenge)))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // ── The issuing endpoint ─────────────────────────────────────────────────

    [Fact]
    public async Task TheChallengeEndpointIssuesADistinctSaltEveryTime()
    {
        await using var factory = new ChallengedApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var first = await FetchAsync(client);
        var second = await FetchAsync(client);

        first.Salt.Length.ShouldBe(32);
        first.Salt.ShouldAllBe(character => Uri.IsHexDigit(character));
        first.Difficulty.ShouldBe(Difficulty);

        // A repeated salt would be a repeated challenge, and single use would
        // then refuse the second honest caller who happened to be handed it.
        second.Salt.ShouldNotBe(first.Salt);

        // A challenge is good exactly once, so a cached copy is one somebody
        // else has already spent.
        (await client.GetAsync("/api/auth/challenge")).Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    // ── Switched off ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WithTheChallengeDisabledTheGuardedEndpointsBehaveExactlyAsBefore()
    {
        // The property that makes this safe to merge ahead of the client that
        // solves it. Every deployment today has no Auth:Challenge configuration
        // at all, and must keep working with none.
        await using var factory = new AccountApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var address = AccountFlow.NewAddress("pow-disabled");

        var registered = await client.PostAsJsonAsync(
            "/api/auth/register", new { email = address, displayName = "Nobody" });

        registered.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Email.CountOf(AccountMessageKind.Verification, address).ShouldBe(1);

        var code = AccountFlow.NewAddress("pow-disabled-code");

        (await client.PostAsJsonAsync("/api/auth/email/code", new { email = code }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        factory.Email.For(code).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task WithTheChallengeDisabledNonsenseHeadersAreIgnored()
    {
        // Not the same statement as the test above, and the more important of
        // the two. A verifier that short-circuits only on the absence of a
        // solution would start refusing a client that had begun sending stale
        // ones — which is exactly what a client rolled out ahead of the switch
        // would be doing.
        await using var factory = new AccountApiFactory(postgres);
        var client = factory.CreateBrowserClient();

        var address = AccountFlow.NewAddress("pow-disabled-junk");

        var response = await RegisterAsync(
            client,
            address,
            new Challenge(new string('f', 32), 99, "not-a-date", "not-a-signature"),
            counter: -1);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Email.CountOf(AccountMessageKind.Verification, address).ShouldBe(1);
    }

    [Fact]
    public async Task TheChallengeEndpointAnswersEvenWhileTheGateIsSwitchedOff()
    {
        // So a client can be written, shipped and exercised against a
        // deployment that has not switched the gate on yet, with one code path
        // rather than two.
        await using var factory = new AccountApiFactory(postgres);

        var challenge = await FetchAsync(factory.CreateBrowserClient());

        challenge.Salt.Length.ShouldBe(32);
        challenge.Signature.Length.ShouldBe(64);
        challenge.Difficulty.ShouldBe(18);
    }

    // ── Refusing to start ────────────────────────────────────────────────────

    [Fact]
    public async Task AHostEnabledWithoutAUsableSecretRefusesToStart()
    {
        // The alternative to failing here is a deployment that believes it is
        // charging for registrations and is not, which nothing would ever
        // report. A host that will not start is noticed within one deploy.
        await using var factory = new MisconfiguredApiFactory(postgres, secret: "too-short");

        var failure = Should.Throw<InvalidOperationException>(() => factory.CreateBrowserClient());

        // Names the key, never the value: startup logs are not a place to put a
        // signing secret.
        failure.Message.ShouldContain("Auth:Challenge:Secret");
        failure.Message.ShouldNotContain("too-short");
    }

    [Fact]
    public async Task AHostWithAnOutOfRangeDifficultyRefusesToStart()
    {
        // Checked even though this host leaves the gate switched off. A
        // difficulty of 40 is a mistake on the day it is typed, and validating
        // it only in the enabled branch would hold the failure back until the
        // operator flipped the switch — which is precisely the moment they are
        // least able to absorb a surprise.
        await using var factory = new MisconfiguredApiFactory(postgres, difficulty: 40);

        Should.Throw<InvalidOperationException>(() => factory.CreateBrowserClient())
            .Message.ShouldContain("Auth:Challenge:Difficulty");
    }

    // ── Driving the protocol ─────────────────────────────────────────────────

    private sealed record Challenge(string Salt, int Difficulty, string ExpiresAt, string Signature);

    private static async Task<Challenge> FetchAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/challenge");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();

        return new Challenge(
            body.GetProperty("salt").GetString()!,
            body.GetProperty("difficulty").GetInt32(),
            body.GetProperty("expiresAt").GetString()!,
            body.GetProperty("signature").GetString()!);
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string address, Challenge challenge, long counter) =>
        PostAsync(
            client,
            "/api/auth/register",
            new { email = address, displayName = "Challenger" },
            challenge,
            counter);

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, object body, Challenge challenge, long counter)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };

        // Spelled out rather than taken from the production constants. These
        // names are a wire contract another repository has to match, so a test
        // that derived them would keep passing through a rename that broke
        // every client.
        request.Headers.Add(SaltHeader, challenge.Salt);
        request.Headers.Add(
            DifficultyHeader, challenge.Difficulty.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(ExpiresHeader, challenge.ExpiresAt);
        request.Headers.Add(SignatureHeader, challenge.Signature);
        request.Headers.Add(CounterHeader, counter.ToString(CultureInfo.InvariantCulture));

        return await client.SendAsync(request);
    }

    /// <summary>The smallest counter that solves a challenge, as a client would find it.</summary>
    private static long Solve(Challenge challenge)
    {
        for (long counter = 0; counter < 100_000_000; counter++)
        {
            if (LeadingZeroBits(Digest(challenge.Salt, counter)) >= challenge.Difficulty)
            {
                return counter;
            }
        }

        throw new InvalidOperationException(
            $"No solution for salt {challenge.Salt} at difficulty {challenge.Difficulty}.");
    }

    /// <summary>
    /// The smallest counter that reaches <paramref name="atLeast"/> zero bits
    /// and no more — a solution to an easier challenge than the one issued.
    /// </summary>
    private static long NearMissCounter(Challenge challenge, int atLeast)
    {
        for (long counter = 0; counter < 100_000_000; counter++)
        {
            var bits = LeadingZeroBits(Digest(challenge.Salt, counter));

            if (bits >= atLeast && bits < challenge.Difficulty)
            {
                return counter;
            }
        }

        throw new InvalidOperationException($"No near miss for salt {challenge.Salt}.");
    }

    /// <summary>A counter that demonstrably does not solve the challenge.</summary>
    private static long UnsolvedCounter(Challenge challenge)
    {
        for (long counter = 0; counter < 100_000_000; counter++)
        {
            if (LeadingZeroBits(Digest(challenge.Salt, counter)) < challenge.Difficulty)
            {
                return counter;
            }
        }

        throw new InvalidOperationException($"Every counter solved salt {challenge.Salt}.");
    }

    private static byte[] Digest(string salt, long counter) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{salt}:{counter}")));

    /// <summary>
    /// How many zero bits a digest opens with, counted one bit at a time.
    /// </summary>
    /// <remarks>
    /// Deliberately the slow, obvious formulation rather than the server's
    /// byte-and-remainder arithmetic. The two are meant to disagree the moment
    /// one of them is wrong, and they cannot do that if the test is written by
    /// copying the implementation.
    /// </remarks>
    private static int LeadingZeroBits(ReadOnlySpan<byte> digest)
    {
        var zeros = 0;

        foreach (var value in digest)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) != 0)
                {
                    return zeros;
                }

                zeros++;
            }
        }

        return zeros;
    }

    /// <summary>
    /// The same host with the proof-of-work gate switched on.
    /// </summary>
    /// <remarks>
    /// Nothing else is changed. In particular the rate limits stay at the
    /// fixture's generous test values, so a refusal in any test here is the
    /// challenge refusing and never the limiter — the two produce different
    /// status codes, but a test that could not tell them apart would be
    /// worthless the day somebody made them agree.
    /// </remarks>
    private sealed class ChallengedApiFactory(
        PostgresFixture postgres,
        int difficulty = Difficulty,
        TimeSpan? lifetime = null)
        : AccountApiFactory(postgres)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseSetting("Auth:Challenge:Enabled", "true");
            builder.UseSetting("Auth:Challenge:Secret", Secret);
            builder.UseSetting(
                "Auth:Challenge:Difficulty", difficulty.ToString(CultureInfo.InvariantCulture));

            if (lifetime is { } configured)
            {
                builder.UseSetting("Auth:Challenge:Lifetime", configured.ToString());
            }
        }
    }

    /// <summary>
    /// A host configured in a way the startup checks are supposed to refuse.
    /// </summary>
    private sealed class MisconfiguredApiFactory(
        PostgresFixture postgres,
        string? secret = null,
        int? difficulty = null)
        : AccountApiFactory(postgres)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            if (secret is not null)
            {
                builder.UseSetting("Auth:Challenge:Enabled", "true");
                builder.UseSetting("Auth:Challenge:Secret", secret);
            }

            if (difficulty is { } configured)
            {
                builder.UseSetting(
                    "Auth:Challenge:Difficulty", configured.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
