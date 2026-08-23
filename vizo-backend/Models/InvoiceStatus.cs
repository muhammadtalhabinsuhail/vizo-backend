using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class InvoiceStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public virtual ICollection<PurchaseInvoice> PurchaseInvoices { get; set; } = new List<PurchaseInvoice>();

    public virtual ICollection<SalesInvoice> SalesInvoices { get; set; } = new List<SalesInvoice>();
}
