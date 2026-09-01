namespace Sw5e.Identity.EmailSignIn;

/// <summary>
/// One issued email sign-in code.
/// </summary>
/// <remarks>
/// <para>
/// A row exists for every request that got past input validation, whether or
/// not the address has an account. That is not bookkeeping for its own sake: it
/// is what makes the per-address budget countable without first asking whether
/// the account exists, and it is what keeps the two branches of the request
/// doing the same amount of work. An implementation that only wrote a row for
/// real accounts would answer a stranger's probe measurably faster than a real
/// user's sign-in.
/// </para>
/// <para>
/// The code itself is never stored. What is stored is a PBKDF2 hash over the
/// address and the code together, with a per-row salt — see
/// <see cref="EmailSignInCodeService"/> for why a deliberately slow hash is
/// worth it over six digits.
/// </para>
/// </remarks>
public sealed class EmailSignInCode
{
    public Guid Id { get; set; }

    /// <summary>
    /// The address the code was issued for, normalised the same way
    /// <c>UserManager</c> normalises one.
    /// </summary>
    /// <remarks>
    /// Normalised rather than raw so that <c>Person@Example.com</c> and
    /// <c>person@example.com</c> share a budget and a code. Two addresses that
    /// reach the same mailbox but count separately would be a rate limit with a
    /// trivial bypass.
    /// </remarks>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>
    /// The account the code signs in, or null when the address had none.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case for a probe or a typo, and the row is written
    /// anyway. A code on such a row can never produce a session: verification
    /// refuses a null account outright, and the message sent to that address
    /// does not carry the code in the first place.
    /// </remarks>
    public Guid? UserId { get; set; }

    /// <summary>Per-row salt for <see cref="CodeHash"/>.</summary>
    public byte[] CodeSalt { get; set; } = [];

    /// <summary>PBKDF2 over the normalised address and the code.</summary>
    public byte[] CodeHash { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the code was spent, or null while it is still live.
    /// </summary>
    /// <remarks>
    /// Set by a conditional update that requires the column to still be null,
    /// so two requests arriving with the same correct code at the same instant
    /// cannot both succeed. Also set — without any code being accepted — when
    /// the attempt budget runs out, and when a newer code for the same address
    /// is redeemed.
    /// </remarks>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>How many codes have been tried against this row.</summary>
    public int Attempts { get; set; }
}
