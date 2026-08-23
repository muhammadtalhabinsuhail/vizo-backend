using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Role
{
    public int RoleId { get; set; }

    public string RoleKey { get; set; } = null!;

    public string RoleName { get; set; } = null!;

    public string Description { get; set; } = null!;

    public string HomePath { get; set; } = null!;

    public bool IsStaffRole { get; set; }

    public bool RequiresEmail { get; set; }

    public bool IsSystem { get; set; }

    public virtual ICollection<DeliveryChannel> DeliveryChannels { get; set; } = new List<DeliveryChannel>();

    public virtual ICollection<User> UserRoleNavigations { get; set; } = new List<User>();

    public virtual ICollection<User> UserRoles { get; set; } = new List<User>();

    public virtual ICollection<Permission> Permissions { get; set; } = new List<Permission>();
}
