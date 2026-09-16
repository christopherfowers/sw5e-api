using Microsoft.AspNetCore.Authorization;

namespace Sw5e.Identity.Authorization;

/// <summary>
/// Demands that the second factor was proved a few minutes ago rather than at
/// some point during the session.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StrongAuthenticationRequirement"/> asks what the session proved.
/// This asks when, and it exists because the two questions stop having the same
/// answer as soon as a session lasts longer than a person stays at their desk.
/// A session here lasts a working day. An administrator who signs in with a
/// passkey at nine has, by the afternoon, a browser that can hand out the
/// Administrator role to anybody, and the passkey is no longer standing between
/// that browser and the site. It is standing between this morning and the site.
/// </para>
/// <para>
/// For nearly everything the platform does, that is the right trade and asking
/// again would be an interruption with nothing behind it. For the handful of
/// actions that change who else can act, it is not: those are the actions an
/// attacker performs first, and they are also the ones a legitimate
/// administrator performs rarely enough that being asked to touch a key is no
/// burden at all.
/// </para>
/// <para>
/// So the window is short and the remedy is immediate. The session is not
/// ended, nothing is revoked, and the account proves the factor it already
/// holds through the re-authentication endpoints, which re-issue the session
/// and stamp it afresh. What was a refusal becomes a prompt.
/// </para>
/// </remarks>
/// <param name="window">
/// How long a proof stays fresh. Measured from the moment the factor was
/// demonstrated, not from the start of the session, and unaffected by the
/// session being renewed.
/// </param>
public sealed class RecentAuthenticationRequirement(TimeSpan window) : IAuthorizationRequirement
{
    /// <summary>How long a proof stays fresh.</summary>
    public TimeSpan Window { get; } = window;
}

/// <summary>Decides <see cref="RecentAuthenticationRequirement"/>.</summary>
/// <remarks>
/// <para>
/// Reads the principal and the clock, and nothing else. There is no store
/// lookup for the same reason the strong authentication handler has none: when
/// the factor was proved is a property of this session, settled when the
/// session was issued, and a query could only return the same answer more
/// slowly.
/// </para>
/// <para>
/// The clock is <see cref="TimeProvider"/> rather than
/// <see cref="DateTimeOffset.UtcNow"/> so that a test can walk a session out of
/// its window instead of sleeping for ten real minutes, which in practice would
/// mean the window went untested.
/// </para>
/// </remarks>
public sealed class RecentAuthenticationHandler(TimeProvider clock)
    : AuthorizationHandler<RecentAuthenticationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RecentAuthenticationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (Sw5eClaims.HasRecentStrongAuthentication(
                context.User, requirement.Window, clock.GetUtcNow()))
        {
            context.Succeed(requirement);
        }

        // No explicit Fail, for the reason given in StrongAuthenticationHandler:
        // leaving the requirement unmet refuses the request, while calling Fail
        // would additionally veto any other handler that might one day satisfy
        // this requirement by some other route.
        return Task.CompletedTask;
    }
}
