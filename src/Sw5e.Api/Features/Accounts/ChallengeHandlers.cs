using Microsoft.AspNetCore.Http.HttpResults;
using Sw5e.Api.Security;

namespace Sw5e.Api.Features.Accounts;

/// <summary>
/// Hands out the proof-of-work challenges that the two expensive anonymous
/// endpoints require.
/// </summary>
/// <remarks>
/// <para>
/// The one endpoint on this API that gives something away to anybody who asks,
/// which is only safe because what it gives away costs nothing to produce and
/// is worthless until somebody spends CPU on it. Issuing a challenge is one
/// draw from the RNG and one HMAC, and — by design — no write of any kind: see
/// <see cref="ProofOfWorkChallenges"/> for why a stored challenge table would
/// have made this endpoint the very denial of service the challenge exists to
/// prevent.
/// </para>
/// <para>
/// It answers whether or not the gate is switched on. A client that had to
/// discover the feature's state before it could ask would need a second flag
/// somewhere to tell it, and would then have two code paths where it should
/// have one; instead it always fetches, always solves, and always sends, and a
/// deployment with the gate off simply does not check the answer.
/// </para>
/// </remarks>
internal static class ChallengeHandlers
{
    public static Ok<ChallengeResponse> Issue(
        HttpContext context,
        ProofOfWorkChallenges challenges)
    {
        // A challenge is good exactly once, so a cached one is a challenge that
        // has already been spent by whoever got the first copy. Said explicitly
        // rather than left to the defaults, because an intermediary that
        // decided this looked like a cacheable GET would break the feature in a
        // way that only shows up under a shared proxy.
        context.Response.Headers.CacheControl = "no-store";

        var challenge = challenges.Issue();

        return TypedResults.Ok(new ChallengeResponse(
            challenge.Salt,
            challenge.Difficulty,
            challenge.ExpiresAt,
            challenge.Signature));
    }
}
