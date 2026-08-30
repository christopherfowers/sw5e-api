using Microsoft.AspNetCore.Identity;

namespace Sw5e.Identity;

/// <summary>
/// A platform role. The set of roles is closed and defined by
/// <see cref="Sw5eRoles"/>; this type exists so the role store has an entity to
/// persist, not so roles can be invented at runtime.
/// </summary>
public sealed class Sw5eRole : IdentityRole<Guid>
{
    public Sw5eRole()
    {
    }

    public Sw5eRole(string roleName) : base(roleName)
    {
    }
}
