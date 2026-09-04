using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PostingStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public virtual ICollection<BankReconciliation> BankReconciliations { get; set; } = new List<BankReconciliation>();

    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();

    public virtual ICollection<GoodsReceipt> GoodsReceipts { get; set; } = new List<GoodsReceipt>();

    public virtual ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
}
