using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Permission
{
    public int PermissionId { get; set; }

    public string PermissionKey { get; set; } = null!;

    public string Label { get; set; } = null!;

    public string GroupName { get; set; } = null!;

    public virtual ICollection<Role> Roles { get; set; } = new List<Role>();
}
