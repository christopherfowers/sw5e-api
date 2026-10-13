using Microsoft.AspNetCore.Identity;
using Sw5e.Domain.Notifications;
using Sw5e.Identity;

namespace Sw5e.Api.Features.Notifications;

/// <summary>
/// Turns an audience into the accounts in it.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in either half it joins. The notification store knows a
/// publish is for the administrators and has no business knowing what makes
/// somebody one; the identity assembly deliberately references nothing of
/// ours, because it is credentials and is kept able to stand alone. This is
/// the composition layer, which already knows about both.
/// </para>
/// <para>
/// Queried on every write rather than cached. Publishing is rare and the
/// administrators are few, so a cache would save nothing measurable and would
/// mean an account promoted this morning hears about nothing until the process
/// restarts.
/// </para>
/// </remarks>
public sealed class RoleNotificationAudienceReader(UserManager<Sw5eUser> users)
    : INotificationAudienceReader
{
    public async Task<IReadOnlyCollection<Guid>> AdministratorIdsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var administrators = await users.GetUsersInRoleAsync(Sw5eRoles.Administrator);

        /*
          Suspended accounts are left out.

          Somebody who cannot sign in cannot read a notification, so the rows
          would sit unread forever and count against a bell nobody can clear.
          More to the point, a suspended administrator is one the platform has
          decided should not be acting on content, and telling them what
          everybody else is publishing is the wrong half of that decision.
        */
        var now = DateTimeOffset.UtcNow;

        return [.. administrators
            .Where(user => user.LockoutEnd is null || user.LockoutEnd <= now)
            .Select(user => user.Id)];
    }
}
