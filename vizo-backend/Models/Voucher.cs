using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Voucher
{
    public int VoucherId { get; set; }

    public string VoucherNo { get; set; } = null!;

    public int VoucherTypeId { get; set; }

    public DateOnly VoucherDate { get; set; }

    public int LocationId { get; set; }

    public int? PartyUserId { get; set; }

    public int? CashBankAccountId { get; set; }

    public decimal Amount { get; set; }

    public int MethodId { get; set; }

    public string? PaymentProvider { get; set; }

    public string? ReferenceNo { get; set; }

    public string? WalletTxnId { get; set; }

    public string Narration { get; set; } = null!;

    public int StatusId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    public virtual Account? CashBankAccount { get; set; }

    public virtual ICollection<Collection> Collections { get; set; } = new List<Collection>();

    public virtual User CreatedByUser { get; set; } = null!;

    public virtual JournalEntry? Entry { get; set; }

    public virtual Location Location { get; set; } = null!;

    public virtual PaymentMethod Method { get; set; } = null!;

    public virtual Party? PartyUser { get; set; }

    public virtual PostingStatus Status { get; set; } = null!;

    public virtual ICollection<VoucherAllocation> VoucherAllocations { get; set; } = new List<VoucherAllocation>();

    public virtual VoucherType VoucherType { get; set; } = null!;
}
