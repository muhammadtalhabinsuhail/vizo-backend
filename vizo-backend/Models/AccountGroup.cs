using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class AccountGroup
{
    public int GroupId { get; set; }

    public string GroupName { get; set; } = null!;

    public bool OnBalanceSheet { get; set; }

    public virtual ICollection<AccountType> AccountTypes { get; set; } = new List<AccountType>();
}
