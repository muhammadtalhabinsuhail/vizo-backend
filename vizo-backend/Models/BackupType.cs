using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class BackupType
{
    public int BackupTypeId { get; set; }

    public string TypeKey { get; set; } = null!;

    public string TypeName { get; set; } = null!;

    public virtual ICollection<BackupHistory> BackupHistories { get; set; } = new List<BackupHistory>();
}
