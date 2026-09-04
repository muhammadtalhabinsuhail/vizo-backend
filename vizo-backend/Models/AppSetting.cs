using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class AppSetting
{
    public int SettingId { get; set; }

    public string SettingGroup { get; set; } = null!;

    public string SettingKey { get; set; } = null!;

    public string SettingValue { get; set; } = null!;

    public string Description { get; set; } = null!;
}
