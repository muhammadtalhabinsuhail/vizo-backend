using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class JournalEntry
{
    public int EntryId { get; set; }

    public string EntryNo { get; set; } = null!;

    public DateOnly EntryDate { get; set; }

    public int EntryTypeId { get; set; }

    public int PeriodId { get; set; }

    public int LocationId { get; set; }

    public string? ReferenceNo { get; set; }

    public string Narration { get; set; } = null!;

    public int StatusId { get; set; }

    public int CreatedByUserId { get; set; }

    public int? PostedByUserId { get; set; }

    public DateOnly CreatedAt { get; set; }

    public virtual User CreatedByUser { get; set; } = null!;

    public virtual JournalEntryType EntryType { get; set; } = null!;

    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();

    public virtual ICollection<GoodsReceipt> GoodsReceipts { get; set; } = new List<GoodsReceipt>();

    public virtual ICollection<JournalEntryLine> JournalEntryLines { get; set; } = new List<JournalEntryLine>();

    public virtual Location Location { get; set; } = null!;

    public virtual FiscalPeriod Period { get; set; } = null!;

    public virtual User? PostedByUser { get; set; }

    public virtual ICollection<PurchaseInvoice> PurchaseInvoices { get; set; } = new List<PurchaseInvoice>();

    public virtual ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();

    public virtual ICollection<SalesInvoice> SalesInvoices { get; set; } = new List<SalesInvoice>();

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();

    public virtual PostingStatus Status { get; set; } = null!;

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
}
