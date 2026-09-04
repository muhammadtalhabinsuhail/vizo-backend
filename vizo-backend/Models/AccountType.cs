using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class AccountType
{
    public int AccountTypeId { get; set; }

    public string TypeName { get; set; } = null!;

    public int GroupId { get; set; }

    public string CodePrefix { get; set; } = null!;

    public short CodeLength { get; set; }

    public bool IsDebitNormal { get; set; }

    public bool IsSystem { get; set; }

    public virtual ICollection<Account> Accounts { get; set; } = new List<Account>();

    public virtual AccountGroup Group { get; set; } = null!;
}
