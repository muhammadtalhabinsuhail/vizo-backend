using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Employee
{
    public int UserId { get; set; }

    public string EmployeeCode { get; set; } = null!;

    public bool IsLocked { get; set; }

    public DateOnly JoinedOn { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public virtual ICollection<BankReconciliation> BankReconciliations { get; set; } = new List<BankReconciliation>();

    public virtual ICollection<Claim> Claims { get; set; } = new List<Claim>();

    public virtual ICollection<Collection> CollectionCollectedByUsers { get; set; } = new List<Collection>();

    public virtual ICollection<Collection> CollectionConfirmedByUsers { get; set; } = new List<Collection>();

    public virtual ICollection<CustomerVisit> CustomerVisits { get; set; } = new List<CustomerVisit>();

    public virtual ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    public virtual ICollection<GoodsReceipt> GoodsReceipts { get; set; } = new List<GoodsReceipt>();

    public virtual ICollection<Party> Parties { get; set; } = new List<Party>();

    public virtual ICollection<PurchaseInvoice> PurchaseInvoices { get; set; } = new List<PurchaseInvoice>();

    public virtual ICollection<PurchaseOrder> PurchaseOrderApprovedByUsers { get; set; } = new List<PurchaseOrder>();

    public virtual ICollection<PurchaseOrder> PurchaseOrderCreatedByUsers { get; set; } = new List<PurchaseOrder>();

    public virtual ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public virtual ICollection<StockTransfer> StockTransferApprovedByUsers { get; set; } = new List<StockTransfer>();

    public virtual ICollection<StockTransfer> StockTransferInitiatedByUsers { get; set; } = new List<StockTransfer>();

    public virtual User User { get; set; } = null!;
}
