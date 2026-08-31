using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sw5e.Identity.EmailSignIn;

/// <summary>What happened when a code was asked for.</summary>
/// <param name="Code">
/// The digits to email, or null when nothing was issued. Never logged, never
/// returned to an HTTP caller, and held only long enough to hand to the mail
/// provider.
/// </param>
/// <param name="ExpiresAt">When the code stops working.</param>
public readonly record struct EmailSignInCodeIssue(string? Code, DateTimeOffset ExpiresAt)
{
    /// <summary>Whether a code was issued at all.</summary>
    public bool Issued => Code is not null;

    /// <summary>The address has spent its budget; nothing was issued or sent.</summary>
    public static EmailSignInCodeIssue Throttled { get; } = new(null, default);
}

/// <summary>The outcome of redeeming a code.</summary>
/// <param name="UserId">The account signed in, when one was.</param>
public readonly record struct EmailSignInCodeRedemption(Guid? UserId)
{
    public bool Succeeded => UserId is not null;

    public static EmailSignInCodeRedemption Failed { get; } = new(null);
}

/// <summary>
/// Issues and redeems the short numeric codes that let somebody sign in from a
/// device with no passkey.
/// </summary>
/// <remarks>
/// <para>
/// This is the weakest credential the platform issues, and it is designed
/// around that fact rather than in spite of it. Six digits is a millionth of
/// the entropy of a passkey; what makes it acceptable is that a code is bound
/// to one address, dies in ten minutes, dies on first use, dies after five
/// wrong guesses, and cannot be requested more than a few times an hour for any
/// one address. Remove any one of those and the arithmetic stops working.
/// </para>
/// <para>
/// Two properties are worth stating explicitly because they are the ones a
/// reviewer should check first.
/// </para>
/// <para>
/// <b>Nothing here learns whether an account exists.</b> The caller passes an
/// address and an optional account; both branches perform one key derivation
/// and one insert, so the work — and therefore the response time — is the same
/// either way. The only path that does less work is the throttled one, and it
/// throttles on the address alone, which is a fact about how often that address
/// has been asked for and not about whether it is registered.
/// </para>
/// <para>
/// <b>The code never appears in a log.</b> It is returned to exactly one caller,
/// which hands it to the mail provider and drops it. The capture provider logs
/// recipients and subjects and deliberately not bodies, for precisely this
/// reason, and every log statement below names the address and the row and
/// never the digits.
/// </para>
/// </remarks>
public sealed class EmailSignInCodeService(
    Sw5eIdentityDbContext database,
    IOptions<Sw5eIdentityOptions> options,
    TimeProvider timeProvider,
    ILogger<EmailSignInCodeService> logger)
{
    /// <summary>
    /// PBKDF2 iterations over the code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberately slow hash over a six-digit secret looks like overkill and
    /// is not. The entire keyspace is a million entries; a plain SHA-256 digest
    /// of a stolen row is reversed by an attacker's laptop in well under a
    /// second, which would turn a read-only database leak into live sessions
    /// for every code in flight. At this cost the same exhaustive search takes
    /// hours of CPU per row, by which time every code in the table has expired.
    /// </para>
    /// <para>
    /// The price is paid twice per code — once on issue, once on redemption —
    /// on endpoints that are rate limited into the low tens of requests per
    /// window. It is not a throughput concern, and it is deliberately paid on
    /// the failure path as well, so the time taken does not say whether a row
    /// was found.
    /// </para>
    /// </remarks>
    private const int HashIterations = 100_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    private readonly Sw5eIdentityOptions _options = options.Value;

    /// <summary>
    /// Issues a code for an address, or declines because that address has had
    /// enough of them lately.
    /// </summary>
    /// <param name="normalizedEmail">
    /// The address, already normalised by the caller so that this and
    /// <c>UserManager</c> agree on what counts as the same address.
    /// </param>
    /// <param name="userId">
    /// The account the address belongs to, or null when it belongs to none.
    /// </param>
    public async Task<EmailSignInCodeIssue> IssueAsync(
        string normalizedEmail,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        // Housekeeping first, and scoped to this one address so it stays a
        // small indexed delete rather than a table sweep. Rows outlive their
        // usefulness by the length of the budget window and no longer: the
        // counting below needs them, nothing else does, and a table of spent
        // credentials is a liability that grows.
        await database.EmailSignInCodes
            .Where(code => code.NormalizedEmail == normalizedEmail &&
                           code.CreatedAt < now - _options.EmailSignInCodeBudgetWindow)
            .ExecuteDeleteAsync(cancellationToken);

        var recent = await database.EmailSignInCodes
            .Where(code => code.NormalizedEmail == normalizedEmail &&
                           code.CreatedAt > now - _options.EmailSignInCodeBudgetWindow)
            .OrderByDescending(code => code.CreatedAt)
            .Select(code => code.CreatedAt)
            .ToListAsync(cancellationToken);

        // Two separate brakes, and they answer different abuses. The cooldown
        // stops a page with a "resend" button — or a script pretending to be
        // one — from putting a message a second into somebody's inbox. The
        // budget stops the same thing spread over an afternoon.
        if (recent.Count > 0 && recent[0] > now - _options.EmailSignInCodeResendCooldown)
        {
            logger.LogInformation(
                "Declined a sign-in code for an address inside its resend cooldown.");
            return EmailSignInCodeIssue.Throttled;
        }

        if (recent.Count >= _options.EmailSignInCodesPerAddress)
        {
            logger.LogInformation(
                "Declined a sign-in code for an address that has spent its budget of {Budget}.",
                _options.EmailSignInCodesPerAddress);
            return EmailSignInCodeIssue.Throttled;
        }

        var code = GenerateCode();
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var expiresAt = now + _options.EmailSignInCodeLifetime;

        database.EmailSignInCodes.Add(new EmailSignInCode
        {
            Id = Guid.NewGuid(),
            NormalizedEmail = normalizedEmail,
            UserId = userId,
            CodeSalt = salt,
            CodeHash = Hash(normalizedEmail, code, salt),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            Attempts = 0,
        });

        await database.SaveChangesAsync(cancellationToken);

        // Note what is absent from this line, and from every other line in this
        // file: the code. A log that carried it would be a credential store
        // with no access control, replicated to wherever logs are shipped.
        logger.LogInformation(
            "Issued an email sign-in code valid until {ExpiresAt:O}.", expiresAt);

        return new EmailSignInCodeIssue(code, expiresAt);
    }

    /// <summary>
    /// Redeems a code, or refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal is the same refusal to the caller. Internally there are
    /// six distinguishable ones — no live code for the address, an expired
    /// code, a spent code, an exhausted attempt budget, wrong digits, and a code
    /// issued for an address with no account — and the log distinguishes them
    /// so an operator can answer a support question. The return value does not,
    /// because each distinction is a fact an attacker would rather have.
    /// </para>
    /// <para>
    /// The path where no row is found still performs a key derivation against a
    /// throwaway salt. Without it, "no code has ever been requested for this
    /// address" would come back an order of magnitude faster than "wrong
    /// digits", which is an oracle for whether somebody is mid-sign-in.
    /// </para>
    /// </remarks>
    public async Task<EmailSignInCodeRedemption> RedeemAsync(
        string normalizedEmail,
        string code,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        // Newest first: a reader who asked twice because the first message was
        // slow is holding the second code, and the first is left to expire.
        var candidate = await database.EmailSignInCodes
            .Where(row => row.NormalizedEmail == normalizedEmail && row.ConsumedAt == null)
            .OrderByDescending(row => row.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            BurnTime(normalizedEmail, code);
            logger.LogInformation("Refused a sign-in code: no live code for that address.");
            return EmailSignInCodeRedemption.Failed;
        }

        if (candidate.ExpiresAt <= now)
        {
            BurnTime(normalizedEmail, code);

            // Consumed rather than left lying about. An expired row that stays
            // unconsumed is a row the query above keeps selecting, which would
            // shadow a code the reader has since requested and is holding right
            // now.
            await ConsumeAsync(candidate.Id, now, cancellationToken);

            logger.LogInformation("Refused a sign-in code that had expired.");
            return EmailSignInCodeRedemption.Failed;
        }

        if (candidate.Attempts >= _options.EmailSignInCodeAttempts)
        {
            BurnTime(normalizedEmail, code);
            await ConsumeAsync(candidate.Id, now, cancellationToken);

            logger.LogInformation(
                "Refused a sign-in code whose {Attempts} attempts were spent.",
                _options.EmailSignInCodeAttempts);

            return EmailSignInCodeRedemption.Failed;
        }

        // The address is part of the hashed input, so a code minted for one
        // address cannot verify against a row for another even if the two rows
        // were somehow confused. The comparison itself is fixed-time.
        var matches = CryptographicOperations.FixedTimeEquals(
            Hash(normalizedEmail, code, candidate.CodeSalt),
            candidate.CodeHash);

        if (!matches)
        {
            // Counted before anything else can happen, so that a client which
            // abandons the connection mid-request has still spent its guess.
            await database.EmailSignInCodes
                .Where(row => row.Id == candidate.Id)
                .ExecuteUpdateAsync(
                    row => row.SetProperty(x => x.Attempts, x => x.Attempts + 1),
                    cancellationToken);

            logger.LogInformation("Refused a sign-in code: the digits did not match.");
            return EmailSignInCodeRedemption.Failed;
        }

        // Single use, decided by the database rather than by this process. The
        // update only lands if the row is still unconsumed, so two requests
        // carrying the same correct code at the same instant produce exactly
        // one session between them — the loser is indistinguishable from
        // somebody who guessed wrong, which is the correct outcome.
        if (!await ConsumeAsync(candidate.Id, now, cancellationToken))
        {
            logger.LogInformation("Refused a sign-in code that was spent concurrently.");
            return EmailSignInCodeRedemption.Failed;
        }

        if (candidate.UserId is not { } userId)
        {
            // The address had no account when the code was issued, so the code
            // was never sent anywhere. Reaching here means somebody guessed the
            // digits of a code that exists only to keep the request path
            // symmetric.
            logger.LogWarning(
                "Refused a sign-in code that was issued for an address with no account.");

            return EmailSignInCodeRedemption.Failed;
        }

        // Everything else outstanding for this address goes with it. Signing in
        // is the end of the sign-in attempt, and a second code still lying
        // around afterwards is a credential nobody is waiting on.
        await database.EmailSignInCodes
            .Where(row => row.NormalizedEmail == normalizedEmail && row.ConsumedAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(x => x.ConsumedAt, now),
                cancellationToken);

        logger.LogInformation("Account {UserId} redeemed an email sign-in code.", userId);

        return new EmailSignInCodeRedemption(userId);
    }

    /// <summary>
    /// Invalidates every live code for an address.
    /// </summary>
    /// <remarks>
    /// Called when an account signs in by some other means. A code sitting in a
    /// mailbox after its owner has already got in is a live credential nobody
    /// is watching.
    /// </remarks>
    public Task DiscardAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        database.EmailSignInCodes
            .Where(row => row.NormalizedEmail == normalizedEmail && row.ConsumedAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(x => x.ConsumedAt, timeProvider.GetUtcNow()),
                cancellationToken);

    private async Task<bool> ConsumeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await database.EmailSignInCodes
            .Where(row => row.Id == id && row.ConsumedAt == null)
            .ExecuteUpdateAsync(
                row => row.SetProperty(x => x.ConsumedAt, now),
                cancellationToken) == 1;

    /// <summary>
    /// A uniformly distributed decimal code.
    /// </summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator.GetInt32(int, int)"/> rather than a
    /// modulo of random bytes, because modulo of a byte range that is not a
    /// multiple of a million biases the low codes, and rather than
    /// <see cref="Random"/>, which is not a cryptographic source and whose
    /// output is predictable from a handful of observations. Padded, so
    /// <c>000042</c> is a code and not a four-digit one.
    /// </remarks>
    private static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(6, '0');

    private static byte[] Hash(string normalizedEmail, string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes($"{normalizedEmail}:{code}"),
            salt,
            HashIterations,
            HashAlgorithm,
            HashBytes);

    /// <summary>
    /// Performs the same key derivation the success path would, and throws the
    /// answer away.
    /// </summary>
    /// <remarks>
    /// The point is the elapsed time, not the value. Without this, the refusal
    /// given to an address that has no code outstanding returns in microseconds
    /// while a genuine wrong guess takes tens of milliseconds, and the
    /// difference is measurable from anywhere on the internet.
    /// </remarks>
    private static void BurnTime(string normalizedEmail, string code) =>
        CryptographicOperations.ZeroMemory(
            Hash(normalizedEmail, code, RandomNumberGenerator.GetBytes(SaltBytes)));
}
