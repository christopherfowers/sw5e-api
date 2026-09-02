using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Sw5e.Api.Security;

/// <summary>
/// How much work an anonymous caller must show before the API will do something
/// expensive on their say-so.
/// </summary>
/// <remarks>
/// <para>
/// Configurable for the same reason the rate limits are: the right difficulty
/// depends on who is actually turning up, and the answer changes faster than
/// the deployment cadence. It is also the setting most likely to need raising
/// in a hurry, in the middle of the abuse it is there to answer, without a
/// rebuild.
/// </para>
/// <para>
/// Off by default. A proof-of-work gate that is switched on the moment the code
/// lands is one that turns a deployment away from the registration endpoint
/// before anybody has confirmed a client can solve it. Every existing
/// deployment therefore keeps behaving exactly as it did until an operator sets
/// both <see cref="Enabled"/> and <see cref="Secret"/>, and the endpoint that
/// issues challenges answers either way so that a client can be built and
/// tested against a deployment where the gate is not yet closed.
/// </para>
/// </remarks>
public sealed class ProofOfWorkOptions
{
    public const string SectionName = "Auth:Challenge";

    /// <summary>The shortest secret this will accept.</summary>
    /// <remarks>
    /// Thirty-two characters, because the secret is the only thing standing
    /// between an attacker and minting their own challenges at difficulty zero.
    /// Everything about a challenge except this key is public — the salt and
    /// the expiry are handed to the caller, and the signed string's shape is in
    /// this file — so a short secret is not obscured by anything and is simply
    /// brute-forced offline from one issued challenge.
    /// </remarks>
    public const int MinimumSecretLength = 32;

    /// <summary>The lowest difficulty that is worth anything.</summary>
    /// <remarks>
    /// Below about eight bits a solution is found on the first handful of
    /// guesses, which costs an attacker nothing and costs the honest client the
    /// round trip anyway. A gate that cannot be felt is worse than no gate: it
    /// looks like a defence in the configuration and is not one.
    /// </remarks>
    public const int MinimumDifficulty = 8;

    /// <summary>The highest difficulty that is still usable by a real person.</summary>
    /// <remarks>
    /// Each bit doubles the expected work. Eighteen bits is roughly a quarter of
    /// a million hashes, which a phone finishes in well under a second;
    /// twenty-four is sixty-four times that, and is already long enough that a
    /// slow device looks broken to whoever is holding it. Above that the setting
    /// stops rationing abuse and starts rationing registration, so it is refused
    /// rather than obeyed.
    /// </remarks>
    public const int MaximumDifficulty = 24;

    /// <summary>
    /// Whether a solved challenge is actually required.
    /// </summary>
    /// <remarks>
    /// When false the verifier accepts everything, including a request that
    /// carries no solution at all. That is deliberate and is what makes this
    /// safe to merge: the route table, the filter and the issuing endpoint are
    /// all live, so the wiring is exercised, while the behaviour every existing
    /// caller sees is unchanged.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// The HMAC key challenges are signed with. Required when
    /// <see cref="Enabled"/> is set, and a secret.
    /// </summary>
    /// <remarks>
    /// There is no default and there will never be one. A default secret in a
    /// public repository is a published secret, and a published signing key
    /// turns this whole mechanism into a formality that any attacker can
    /// satisfy without hashing anything.
    /// </remarks>
    public string? Secret { get; set; }

    /// <summary>Leading zero <em>bits</em> a solution must produce.</summary>
    public int Difficulty { get; set; } = 18;

    /// <summary>
    /// How long an issued challenge stays good for.
    /// </summary>
    /// <remarks>
    /// Long enough that a slow device solving a hard challenge is not raced by
    /// its own expiry, and short enough to bound the spent-salt set and the
    /// window in which a stolen challenge is worth anything.
    /// </remarks>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>A challenge, exactly as it goes out on the wire.</summary>
/// <param name="Salt">Thirty-two hex characters from the system RNG.</param>
/// <param name="Difficulty">Leading zero bits the solution must produce.</param>
/// <param name="ExpiresAt">
/// ISO-8601, round-trip format. A string rather than a
/// <see cref="DateTimeOffset"/> all the way through, because it is covered by
/// the signature and therefore has to come back byte for byte — see
/// <see cref="ProofOfWorkChallenges"/> for why that is not fussiness.
/// </param>
/// <param name="Signature">Lowercase hex HMAC-SHA256 over the three above.</param>
internal sealed record IssuedChallenge(
    string Salt,
    int Difficulty,
    string ExpiresAt,
    string Signature);

/// <summary>A challenge handed back with the counter that solves it.</summary>
internal sealed record ProofOfWorkSolution(
    string Salt,
    int Difficulty,
    string ExpiresAt,
    string Signature,
    long Counter);

/// <summary>
/// Issues proof-of-work challenges, and decides whether a solution to one is
/// worth anything.
/// </summary>
/// <remarks>
/// <para>
/// The problem this answers is narrow and worth stating precisely. Two
/// endpoints — opening a registration and asking for a sign-in code — can be
/// reached by a stranger and cause the platform to do real work on their
/// say-so: a database write in the first case, and an outbound message to an
/// address the caller chose in the second. Rate limiting already caps how fast
/// one caller can do that. It cannot touch an attacker with ten thousand source
/// addresses, because every one of them is inside its own budget. Proof of work
/// is the half that does not care where the request came from: it charges CPU
/// per request rather than per caller, and CPU is the one resource a botnet
/// cannot get for free by spreading out.
/// </para>
/// <para>
/// Nothing is stored when a challenge is issued, and that is the central design
/// decision. A server-side table of outstanding challenges would be a table an
/// anonymous caller can fill by asking for challenges, which turns the
/// anti-abuse mechanism into the abuse. Instead the challenge carries its own
/// proof of provenance: the signature is over the salt, the difficulty and the
/// expiry together, so a caller can neither invent a challenge nor edit one we
/// gave them, and the server needs to remember nothing at all until the moment
/// a solution comes back.
/// </para>
/// <para>
/// The one thing that is remembered is which salts have been spent, and only
/// from the point a solution is accepted. See <see cref="Verify"/>.
/// </para>
/// <para>
/// Registered as a singleton, because the spent-salt set has to be shared by
/// every request in the process for single use to mean anything.
/// </para>
/// </remarks>
internal sealed class ProofOfWorkChallenges
{
    /// <summary>Sixteen bytes, which is the thirty-two hex characters the protocol specifies.</summary>
    /// <remarks>
    /// The salt is not a secret — it goes out in the response — so its length
    /// is about collisions rather than guessing. 128 bits means two honest
    /// clients never draw the same salt, which matters because the second one
    /// to arrive would be refused as a replay.
    /// </remarks>
    private const int SaltBytes = 16;

    /// <summary>
    /// How far past the configured lifetime an expiry may sit before it is
    /// treated as forged.
    /// </summary>
    /// <remarks>
    /// The upper bound exists so that lowering the lifetime takes effect on
    /// challenges already in flight rather than only on new ones. The skew
    /// exists because this process's clock and the clock that stamped the
    /// challenge may not be the same one — a deployment with two instances
    /// behind a load balancer routinely issues on one and verifies on the
    /// other — and a minute of drift between two NTP-disciplined machines
    /// should not read as an attack.
    /// </remarks>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>How often expired salts are swept out of the spent set.</summary>
    /// <remarks>
    /// On a timer rather than on every verification: sweeping is linear in the
    /// size of the set, and doing it per request would hand an attacker a way
    /// to make each request cost more by making the previous ones expensive.
    /// </remarks>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ProofOfWorkOptions _options;
    private readonly TimeProvider _clock;
    private readonly byte[] _key;

    /// <summary>
    /// The salts that have already bought a request, and when each stops
    /// mattering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In memory, per process, and lost on restart. That is a real limitation
    /// and it is the right trade, so it is worth being exact about what it does
    /// and does not cost.
    /// </para>
    /// <para>
    /// What it costs: for the few seconds after a restart — or on a second
    /// instance that never saw the first one's traffic — a solution that was
    /// already spent can be spent again. The blast radius is bounded by the
    /// expiry, because every other check still applies: the replay must carry a
    /// signature we issued, at the current difficulty, that has not yet
    /// expired. So the worst case is one extra use per outstanding challenge
    /// per instance, inside a ten-minute window, on top of a rate limit that is
    /// untouched by any of this.
    /// </para>
    /// <para>
    /// What the alternative costs: a shared store — the database, or a cache
    /// server — written to by an anonymous, unauthenticated request. That is a
    /// write an attacker can trigger at will, on the exact endpoints this
    /// mechanism exists to protect, which is a strictly worse position than the
    /// one above. It would also make the registration endpoint fail when the
    /// cache was unavailable, converting a defence into a dependency.
    /// </para>
    /// <para>
    /// Ordinal comparison because a salt is hex, and a culture-aware comparison
    /// on a security key is a way to have two spellings of the same value.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _spent = new(StringComparer.Ordinal);

    private long _nextSweepTicks;

    public ProofOfWorkChallenges(IOptions<ProofOfWorkOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;

        // A configured secret when the gate is on; an ephemeral one when it is
        // off. The ephemeral key is not a default secret and could never become
        // one: it is drawn from the RNG at startup, never leaves the process,
        // and is different in every instance and after every restart. It exists
        // only so that a deployment with the gate switched off still answers
        // /challenge with a well-formed, internally consistent document that a
        // client can be developed against. Nothing verifies against it, because
        // with the gate off nothing verifies at all.
        //
        // AddSw5eProofOfWork has already refused to start a host that is
        // enabled without a usable secret, so the null branch here is only ever
        // the disabled one.
        _key = _options.Enabled && !string.IsNullOrEmpty(_options.Secret)
            ? Encoding.UTF8.GetBytes(_options.Secret)
            : RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>Whether a solution is actually required of anybody.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Mints a fresh challenge. Stores nothing.</summary>
    public IssuedChallenge Issue()
    {
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SaltBytes));
        var difficulty = _options.Difficulty;

        // Round-trip format, in UTC, produced once and carried as a string from
        // here to the signature to the response body. The temptation is to hold
        // a DateTimeOffset and format it at each end; that quietly breaks,
        // because System.Text.Json trims trailing zeros from the fractional
        // second and "O" does not, so the string the client is handed would not
        // be the string that was signed and every solution would be rejected.
        var expiresAt = _clock.GetUtcNow()
            .ToUniversalTime()
            .Add(_options.Lifetime)
            .ToString("O", CultureInfo.InvariantCulture);

        return new IssuedChallenge(salt, difficulty, expiresAt, Sign(salt, difficulty, expiresAt));
    }

    /// <summary>
    /// Decides whether a solution entitles the caller to the request they
    /// attached it to, and spends it if so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One boolean out, for every way this can fail. The caller cannot be told
    /// which check refused them for the same reason no sign-in failure in this
    /// API is itemised: "your signature was fine but the salt was spent" tells
    /// somebody probing the mechanism exactly which of their assumptions was
    /// right, and there is no legitimate client that needs to know — the honest
    /// answer to every refusal is to fetch a new challenge and solve it.
    /// </para>
    /// <para>
    /// The order of the checks is chosen so that the cheap ones that reject
    /// forgeries run before the expensive one that hashes, and so that the salt
    /// is only spent once everything else has passed. Spending earlier would
    /// let anybody holding an intercepted challenge burn it by submitting a
    /// deliberately wrong counter.
    /// </para>
    /// </remarks>
    public bool Verify(ProofOfWorkSolution? solution)
    {
        // The switch. With the gate off, a request carrying no solution at all
        // is accepted, which is what keeps every existing deployment and every
        // existing test working unchanged.
        if (!_options.Enabled)
        {
            return true;
        }

        if (solution is null)
        {
            return false;
        }

        // 1. Provenance. Everything below only means something once we know
        //    this is a challenge we issued and that none of its fields have
        //    been edited since.
        if (!SignatureMatches(solution))
        {
            return false;
        }

        // 2. Freshness, in both directions. A challenge past its expiry is
        //    refused, and so is one claiming an expiry further out than the
        //    configured lifetime allows — which is how lowering the lifetime
        //    takes effect immediately instead of waiting out the challenges
        //    already issued under the old one.
        if (!DateTimeOffset.TryParse(
                solution.ExpiresAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            return false;
        }

        var now = _clock.GetUtcNow();

        if (expiresAt <= now || expiresAt > now + _options.Lifetime + ClockSkew)
        {
            return false;
        }

        // 3. The difficulty is pinned to whatever is configured now, not to
        //    whatever was configured when the challenge was signed. Without
        //    this, raising the difficulty during an attack would achieve
        //    nothing for the length of a lifetime: the attacker would keep
        //    submitting the easy challenges they had already been issued, and
        //    the signature check would happily agree that we issued them.
        if (solution.Difficulty != _options.Difficulty)
        {
            return false;
        }

        // 4. The work itself. Last of the stateless checks because it is the
        //    only one that hashes, and there is no reason to hash on behalf of
        //    somebody whose challenge we did not issue.
        if (!HasWork(solution.Salt, solution.Counter, solution.Difficulty))
        {
            return false;
        }

        // 5. Single use. Without this the mechanism collapses: one solved
        //    challenge would buy an unlimited number of registrations for the
        //    length of its lifetime, which is precisely the mass-signup case
        //    this is here to make expensive. TryAdd is the spend, and it is
        //    atomic, so two requests racing with the same salt cannot both be
        //    told they were first.
        Sweep(now);

        return _spent.TryAdd(solution.Salt, expiresAt);
    }

    /// <summary>
    /// Whether SHA-256 of <c>{salt}:{counter}</c> opens with the required
    /// number of zero bits.
    /// </summary>
    /// <remarks>
    /// Bits, not hex characters, because counting characters can only express
    /// difficulty in steps of four bits — every increment would multiply the
    /// attacker's cost by sixteen and the honest client's with it, which leaves
    /// no setting between "barely noticeable" and "unusable on a phone".
    /// </remarks>
    public static bool HasWork(string salt, long counter, int difficulty)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{salt}:{counter}")),
            hash);

        return HasLeadingZeroBits(hash, difficulty);
    }

    private static bool HasLeadingZeroBits(ReadOnlySpan<byte> hash, int bits)
    {
        // A difficulty wider than the digest can never be satisfied. Configured
        // difficulty is bounded far below this at startup; the guard is here so
        // that a future caller passing something absurd gets a refusal rather
        // than an index out of range.
        if (bits < 0 || bits > hash.Length * 8)
        {
            return false;
        }

        var wholeBytes = bits / 8;

        for (var index = 0; index < wholeBytes; index++)
        {
            if (hash[index] != 0)
            {
                return false;
            }
        }

        var remainder = bits % 8;

        // The byte straddling the boundary: only the top `remainder` bits are
        // required to be zero, so shift the rest out rather than comparing the
        // whole byte. Getting this wrong in the lenient direction — ignoring
        // the partial byte — silently rounds every difficulty down to a
        // multiple of eight.
        return remainder == 0 || (hash[wholeBytes] >> (8 - remainder)) == 0;
    }

    private bool SignatureMatches(ProofOfWorkSolution solution)
    {
        var expected = Compute(solution.Salt, solution.Difficulty, solution.ExpiresAt);

        Span<byte> supplied = stackalloc byte[HMACSHA256.HashSizeInBytes];

        // Length and shape first. These are not secrets — the signature's size
        // is fixed and public — and rejecting a malformed value here keeps the
        // comparison below operating on two buffers of equal, known length,
        // which is what FixedTimeEquals requires to be constant time.
        if (Convert.FromHexString(solution.Signature, supplied, out var consumed, out var written)
                != OperationStatus.Done ||
            consumed != solution.Signature.Length ||
            written != expected.Length)
        {
            return false;
        }

        // Fixed time, not string equality and not SequenceEqual. An ordinary
        // comparison returns as soon as it finds a differing byte, so how long
        // it took reveals how many leading bytes were right — and an attacker
        // who can measure that recovers a valid signature one byte at a time,
        // in a few hundred requests per byte, without ever learning the key.
        // The endpoints this guards are anonymous and unlimited in how often
        // they can be asked to compare, which is exactly the setting where that
        // attack is practical.
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private string Sign(string salt, int difficulty, string expiresAt) =>
        Convert.ToHexStringLower(Compute(salt, difficulty, expiresAt));

    private byte[] Compute(string salt, int difficulty, string expiresAt) =>
        HMACSHA256.HashData(
            _key,
            Encoding.UTF8.GetBytes(
                string.Create(CultureInfo.InvariantCulture, $"{salt}:{difficulty}:{expiresAt}")));

    /// <summary>
    /// Drops spent salts that can no longer be replayed anyway, at most once
    /// per <see cref="SweepInterval"/>.
    /// </summary>
    /// <remarks>
    /// An entry whose expiry has passed is already refused by the freshness
    /// check, so keeping it buys nothing and costs memory that an anonymous
    /// caller decides the size of. Only one thread does the work — the rest see
    /// the deadline has already moved and carry on — because a sweep is
    /// housekeeping and a request should never wait behind one.
    /// </remarks>
    private void Sweep(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);

        if (now.UtcTicks < due)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _nextSweepTicks,
                now.Add(SweepInterval).UtcTicks,
                due) != due)
        {
            return;
        }

        foreach (var entry in _spent)
        {
            if (entry.Value <= now)
            {
                // Remove the exact pair. Removing by key alone could discard an
                // entry another thread had just re-added, handing that caller a
                // second use of a salt they had already spent.
                ((ICollection<KeyValuePair<string, DateTimeOffset>>)_spent).Remove(entry);
            }
        }
    }
}

/// <summary>
/// Registers the proof-of-work challenge, and refuses to start a host that has
/// been configured to use one it cannot honour.
/// </summary>
internal static class ProofOfWorkServiceCollectionExtensions
{
    public static IServiceCollection AddSw5eProofOfWork(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(ProofOfWorkOptions.SectionName);

        // Bound to a throwaway instance so the configuration can be checked
        // before the host exists, which is the only point at which refusing is
        // still cheap. The container binds the section again below.
        var options = section.Get<ProofOfWorkOptions>() ?? new ProofOfWorkOptions();

        // Checked whether or not the gate is switched on. A difficulty of 400
        // is a mistake on the day it is written, not on the day somebody
        // switches the feature on, and validating it only in the enabled branch
        // means the mistake surfaces during the incident the operator was
        // reaching for the setting to answer.
        if (options.Difficulty < ProofOfWorkOptions.MinimumDifficulty ||
            options.Difficulty > ProofOfWorkOptions.MaximumDifficulty)
        {
            throw new InvalidOperationException(
                $"{ProofOfWorkOptions.SectionName}:{nameof(ProofOfWorkOptions.Difficulty)} is " +
                $"{options.Difficulty}. It must be between {ProofOfWorkOptions.MinimumDifficulty} " +
                $"and {ProofOfWorkOptions.MaximumDifficulty} leading zero bits.");
        }

        if (options.Lifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{ProofOfWorkOptions.SectionName}:{nameof(ProofOfWorkOptions.Lifetime)} is " +
                $"{options.Lifetime}. A challenge that has expired before it is issued can never " +
                "be solved.");
        }

        // Fail closed, and fail loudly. The alternative — starting with a
        // generated key, or with the gate quietly switched back off — is a
        // deployment that believes it is protected and is not, which is worse
        // than one that will not start. The message names the key rather than
        // the value, because the value is a secret and startup logs are not.
        if (options.Enabled &&
            (options.Secret is null || options.Secret.Length < ProofOfWorkOptions.MinimumSecretLength))
        {
            throw new InvalidOperationException(
                $"{ProofOfWorkOptions.SectionName}:{nameof(ProofOfWorkOptions.Enabled)} is set, so " +
                $"{ProofOfWorkOptions.SectionName}:{nameof(ProofOfWorkOptions.Secret)} must be " +
                $"configured with at least {ProofOfWorkOptions.MinimumSecretLength} characters. " +
                "There is no default: a signing key committed to a public repository would let " +
                "anyone mint their own challenges.");
        }

        services.Configure<ProofOfWorkOptions>(section);

        // Singleton, because the set of spent salts is the state that makes
        // single use mean anything and a scoped instance would give every
        // request a fresh, empty one.
        services.AddSingleton<ProofOfWorkChallenges>();

        return services;
    }
}
