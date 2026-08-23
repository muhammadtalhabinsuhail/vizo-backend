using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PaymentMethod
{
    public int MethodId { get; set; }

    public string MethodKey { get; set; } = null!;

    public string MethodName { get; set; } = null!;

    public string MethodKind { get; set; } = null!;

    public bool IsActive { get; set; }

    public virtual ICollection<Collection> Collections { get; set; } = new List<Collection>();

    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();

    public virtual ICollection<PurchaseInvoice> PurchaseInvoices { get; set; } = new List<PurchaseInvoice>();

    public virtual ICollection<SalesInvoice> SalesInvoices { get; set; } = new List<SalesInvoice>();

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
}
