using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class UserPreference
{
    public int UserId { get; set; }

    public string PrefKey { get; set; } = null!;

    public string PrefValue { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
