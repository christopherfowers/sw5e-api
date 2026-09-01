using Microsoft.AspNetCore.Authorization;

namespace Sw5e.Identity.Authorization;

/// <summary>
/// Demands that the session was established with a passkey or an authenticator
/// app rather than with an emailed code alone.
/// </summary>
/// <remarks>
/// <para>
/// Attached to the policies behind the roles that can change what other people
/// read — <see cref="Sw5ePolicies.Contribute"/> and
/// <see cref="Sw5ePolicies.Administer"/> — and deliberately not to
/// <see cref="Sw5ePolicies.SignedIn"/>. Managing one's own profile is exactly
/// the thing an emailed code is for; publishing content to a site that replaced
/// a community's reference material, or handing somebody else the ability to,
/// is not.
/// </para>
/// <para>
/// This is what "elevated roles require a second factor" actually means once it
/// is written down as a rule a server can apply. Requiring merely that the
/// account <em>has</em> a second factor enrolled would be satisfied by an
/// administrator with a passkey who signed in from a library computer with a
/// mailbox code, which is the case the requirement exists to stop. Requiring
/// that a second factor was <em>used</em> covers both: an account with nothing
/// enrolled can never produce a qualifying session, and an account with
/// something enrolled has to actually use it.
/// </para>
/// <para>
/// The consequence for a person granted Contributor while holding neither is
/// that they keep the role and are told to enrol — see the role assignment
/// handler, which emails them, and the account endpoint, which reports the
/// requirement. They are not locked out of their account, and the requirement
/// is not quietly relaxed for them.
/// </para>
/// </remarks>
public sealed class StrongAuthenticationRequirement : IAuthorizationRequirement;

/// <summary>Decides <see cref="StrongAuthenticationRequirement"/>.</summary>
/// <remarks>
/// Reads nothing but the principal. There is no store lookup here on purpose:
/// the fact being checked — how this session was authenticated — was settled
/// when the session was created and cannot change while it lasts, so a lookup
/// would be a per-request query that could only ever return the same answer, or
/// worse, a different one.
/// </remarks>
public sealed class StrongAuthenticationHandler
    : AuthorizationHandler<StrongAuthenticationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StrongAuthenticationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Sw5eClaims.HasStrongAuthentication(context.User))
        {
            context.Succeed(requirement);
        }

        // No explicit Fail. Leaving the requirement unmet is enough to refuse,
        // and calling Fail would additionally veto any other handler that might
        // legitimately satisfy this requirement in future — turning a policy
        // that can grow a second route to satisfaction into one that cannot.
        return Task.CompletedTask;
    }
}
