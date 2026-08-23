using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Party
{
    public int UserId { get; set; }

    public string PartyCode { get; set; } = null!;

    public string LegalName { get; set; } = null!;

    public string? DisplayName { get; set; }

    public int CategoryId { get; set; }

    public int CityId { get; set; }

    public string? AddressLine { get; set; }

    public string? AltPhone { get; set; }

    public string? Industry { get; set; }

    public string? Ntn { get; set; }

    public string? Strn { get; set; }

    public string? Cnic { get; set; }

    public decimal CreditLimit { get; set; }

    public int CreditDays { get; set; }

    public int HoldPolicyId { get; set; }

    public decimal OpeningBalance { get; set; }

    public int? SalesPersonUserId { get; set; }

    public int? DefaultLocationId { get; set; }

    public char Rating { get; set; }

    public string? Notes { get; set; }

    public virtual PartyCategory Category { get; set; } = null!;

    public virtual City City { get; set; } = null!;

    public virtual ICollection<Claim> ClaimCustomerUsers { get; set; } = new List<Claim>();

    public virtual ICollection<Claim> ClaimSupplierUsers { get; set; } = new List<Claim>();

    public virtual ICollection<Collection> Collections { get; set; } = new List<Collection>();

    public virtual ICollection<CustomerVisit> CustomerVisits { get; set; } = new List<CustomerVisit>();

    public virtual Location? DefaultLocation { get; set; }

    public virtual ICollection<GoodsReceipt> GoodsReceipts { get; set; } = new List<GoodsReceipt>();

    public virtual CreditHoldPolicy HoldPolicy { get; set; } = null!;

    public virtual ICollection<JournalEntryLine> JournalEntryLines { get; set; } = new List<JournalEntryLine>();

    public virtual ICollection<PurchaseInvoice> PurchaseInvoices { get; set; } = new List<PurchaseInvoice>();

    public virtual ICollection<PurchaseOrder> PurchaseOrders { get; set; } = new List<PurchaseOrder>();

    public virtual ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();

    public virtual ICollection<SalesInvoice> SalesInvoices { get; set; } = new List<SalesInvoice>();

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual Employee? SalesPersonUser { get; set; }

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();

    public virtual User User { get; set; } = null!;

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
}
