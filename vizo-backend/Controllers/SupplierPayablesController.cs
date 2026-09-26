using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// What the ACCOUNTANT may still see of purchasing: what is owed to suppliers.
///
/// Since 26 Sep purchases are the Super Admin's alone -- the owner: "koi bhi
/// purchases ki koi bhi cheez, koi bhi item kitne mein khareeda hai ... kisi bhi
/// role ko nahi dikhni chahiye". PurchasesController is locked to that role.
/// But suppliers still have to be paid, and paying them is the accountant's job,
/// so the two figures that job needs -- total payable and which bills fall due --
/// live here, at the same URLs they always had, open to both roles. Neither
/// carries a line, a quantity or a unit cost: a bill's total says what we owe,
/// not what an item cost.
///
/// Same route prefix as PurchasesController on purpose, so no caller changes.
/// </summary>
[Route("api/purchases")]
[ApiController]
[Authorize(Roles = "super-admin,accountant")]
public class SupplierPayablesController : ApiControllerBase
{
    public SupplierPayablesController(AppDbContext db, IConfiguration cfg,
        ILogger<SupplierPayablesController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /// <summary>
    /// The figures the supplier list shows above its table.
    ///
    /// "Open POs" and "Pending GRNs" went with the purchase-order statuses on
    /// 26 Sep: an order is received the moment it is written, so nothing is
    /// ever open or pending. What is left is what matters to the person paying.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetPurchasesSummary()
    {
        try
        {
            var bills = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.Status.StatusKey != "VOID")
                .Select(i => new
                {
                    total = i.TotalAmount,
                    due = i.DueDate,
                    paid = i.VoucherAllocations
                        .Where(a => a.Voucher.Status.StatusKey == "POSTED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            var open = bills.Where(b => b.total - b.paid > 0).ToList();
            return Ok(new
            {
                openBills = open.Count,
                overdueBills = open.Count(b => b.due < Today()),
                payableTotal = open.Sum(b => b.total - b.paid)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchases summary");
        }
    }

    /// <summary>
    /// Supplier payables due inside `withinDays`, oldest first. Feeds the
    /// accountant's payables screen and the payment reminders.
    /// </summary>
    [HttpGet("payables")]
    public async Task<IActionResult> GetPayables([FromQuery] int withinDays = 30)
    {
        try
        {
            var cutoff = Today().AddDays(withinDays);

            var rows = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.DueDate <= cutoff)
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplierInvoiceNo = i.SupplierInvoiceNo,
                    supplierId = i.SupplierUserId,
                    supplierName = (i.SupplierUser.DisplayName ?? i.SupplierUser.LegalName),
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var open = rows.Where(r => r.total - r.paid > 0)
                .OrderBy(r => r.dueDate)
                .Select(r => new
                {
                    r.id, r.invoiceNo, r.supplierInvoiceNo, r.supplierId, r.supplierName,
                    supplierInitials = Initials(r.supplierName),
                    r.invoiceDate, r.dueDate, r.total, r.paid,
                    balance = r.total - r.paid,
                    daysToDue = r.dueDate.DayNumber - Today().DayNumber,
                    isOverdue = r.dueDate < Today()
                })
                .ToList();

            return Ok(new
            {
                withinDays,
                count = open.Count,
                totalDue = open.Sum(o => o.balance),
                overdueCount = open.Count(o => o.isOverdue),
                overdueTotal = open.Where(o => o.isOverdue).Sum(o => o.balance),
                items = open
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load supplier payables");
        }
    }
}
