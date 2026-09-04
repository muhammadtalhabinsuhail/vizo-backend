using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Location
{
    public int LocationId { get; set; }

    public string LocationCode { get; set; } = null!;

    public string LocationName { get; set; } = null!;

    public int KindId { get; set; }

    public int CityId { get; set; }

    public string AddressLine { get; set; } = null!;

    public int? InChargeUserId { get; set; }

    public bool IsActive { get; set; }

    public bool IsDefault { get; set; }

    public bool ExcludeFromSellable { get; set; }

    public virtual ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();

    public virtual City City { get; set; } = null!;

    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();

    public virtual ICollection<GoodsReceipt> GoodsReceipts { get; set; } = new List<GoodsReceipt>();

    public virtual User? InChargeUser { get; set; }

    public virtual ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();

    public virtual LocationKind Kind { get; set; } = null!;

    public virtual ICollection<Party> Parties { get; set; } = new List<Party>();

    public virtual ICollection<PurchaseOrder> PurchaseOrders { get; set; } = new List<PurchaseOrder>();

    public virtual ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();

    public virtual ICollection<SalesInvoice> SalesInvoices { get; set; } = new List<SalesInvoice>();

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual ICollection<SalesReturnItem> SalesReturnItems { get; set; } = new List<SalesReturnItem>();

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public virtual ICollection<StockBalance> StockBalances { get; set; } = new List<StockBalance>();

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();

    public virtual ICollection<StockTransfer> StockTransferFromLocations { get; set; } = new List<StockTransfer>();

    public virtual ICollection<StockTransfer> StockTransferToLocations { get; set; } = new List<StockTransfer>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();

    public virtual ICollection<User> UsersNavigation { get; set; } = new List<User>();
}
