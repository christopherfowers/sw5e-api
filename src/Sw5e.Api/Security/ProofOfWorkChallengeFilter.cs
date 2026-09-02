using System.Globalization;
using Sw5e.Api.Features.Accounts;

namespace Sw5e.Api.Security;

/// <summary>
/// The header names a solved challenge travels in.
/// </summary>
/// <remarks>
/// <para>
/// Headers rather than body fields, for three reasons. The solution is a
/// statement about the request rather than about the account being registered,
/// so putting it in the body would mean every guarded endpoint's documented
/// request schema grows five fields that have nothing to do with what it does —
/// and this repository's request shapes are a published contract that another
/// repository generates a client from. It also means the check can be made
/// before the body is looked at, by a filter that does not need to know what
/// endpoint it is guarding. And a custom request header cannot be attached to
/// the kind of cross-origin request an HTML form can make, so it is one more
/// thing a forged submission cannot produce.
/// </para>
/// <para>
/// The <c>X-</c> prefix is deprecated for standardised headers and perfectly
/// ordinary for application-private ones, which this is.
/// </para>
/// </remarks>
internal static class ProofOfWorkHeaders
{
    public const string Salt = "X-Sw5e-Challenge-Salt";
    public const string Difficulty = "X-Sw5e-Challenge-Difficulty";
    public const string ExpiresAt = "X-Sw5e-Challenge-Expires";
    public const string Signature = "X-Sw5e-Challenge-Signature";
    public const string Counter = "X-Sw5e-Challenge-Counter";
}

/// <summary>
/// Refuses a request that has not paid for itself.
/// </summary>
/// <remarks>
/// <para>
/// Applied per route rather than to the whole account group, because most of
/// that group is either authenticated or already cheap, and a challenge in
/// front of a sign-in is a tax on the one person the platform actually wants to
/// let in. The two routes it does guard are the two an anonymous stranger can
/// use to make the platform spend something: <c>/register</c> writes an account,
/// and <c>/email/code</c> sends a message to an address the caller chose.
/// </para>
/// <para>
/// This sits on top of the rate limiter and replaces none of it. They answer
/// different attackers, and either one alone leaves the other's attacker
/// untouched: the limiter stops one machine going fast and cannot see a botnet
/// spread thin, while proof of work charges every request the same regardless
/// of where it came from and does nothing about a single caller who is willing
/// to pay repeatedly.
/// </para>
/// <para>
/// Runs after the group's <see cref="CrossSiteRequestFilter"/>, so a forged
/// cross-site request is refused before this asks it for anything — the cheaper
/// and more certain check goes first.
/// </para>
/// </remarks>
internal sealed class ProofOfWorkChallengeFilter(ProofOfWorkChallenges challenges) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (challenges.Verify(Read(context.HttpContext.Request)))
        {
            return await next(context);
        }

        return AccountProblems.ChallengeRequired;
    }

    /// <summary>
    /// Pulls the five header values out, or null if any of them is missing or
    /// malformed.
    /// </summary>
    /// <remarks>
    /// Null rather than a partially populated solution, and the verifier treats
    /// null as a refusal. Nothing is validated here beyond "these five values
    /// are present and are the right kind of thing"; every decision that
    /// matters is made in one place, by the verifier, so that a future caller
    /// of this cannot accidentally reimplement half of it.
    /// </remarks>
    private static ProofOfWorkSolution? Read(HttpRequest request)
    {
        var headers = request.Headers;

        var salt = headers[ProofOfWorkHeaders.Salt].ToString();
        var expiresAt = headers[ProofOfWorkHeaders.ExpiresAt].ToString();
        var signature = headers[ProofOfWorkHeaders.Signature].ToString();

        if (salt.Length == 0 || expiresAt.Length == 0 || signature.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(
                headers[ProofOfWorkHeaders.Difficulty].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var difficulty))
        {
            return null;
        }

        // NumberStyles.None on both: no sign, no thousands separator, no
        // leading or trailing space. The protocol says a non-negative integer,
        // and accepting anything else means the verifier is asked about values
        // the protocol does not define — a negative counter, which cannot solve
        // anything, or a spelling the client never sent.
        if (!long.TryParse(
                headers[ProofOfWorkHeaders.Counter].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var counter))
        {
            return null;
        }

        return new ProofOfWorkSolution(salt, difficulty, expiresAt, signature, counter);
    }
}
