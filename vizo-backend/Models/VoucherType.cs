using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class VoucherType
{
    public int VoucherTypeId { get; set; }

    public string TypeCode { get; set; } = null!;

    public string TypeName { get; set; } = null!;

    public bool IsReceipt { get; set; }

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
}
