using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The 80 mm thermal receipt for a sale invoice -- the "Print Bill" button on
/// the order page (2 October). The A4 invoice is still served by
/// SalesController at /sales/invoices/{id}/pdf and is now labelled
/// "Print Invoice"; see Documents/ReceiptPdf.cs for why there are two.
///
/// WHY A CONTROLLER OF ITS OWN rather than one more action in
/// SalesController: that file is five thousand lines and several people edit
/// it at once. This is a read-only, one-endpoint document that shares nothing
/// with the order workflow except the visibility rule, so it lives apart and
/// the rule is restated here, word for word, instead of reached into.
///
/// The route sits under /api/sales so it reads as the sibling of the bill
/// endpoints, and the same policies guard it.
/// </summary>
[Route("api/sales")]
[ApiController]
[Authorize(Policy = "Staff")]
public class ReceiptController : ApiControllerBase
{
    public ReceiptController(AppDbContext db, IConfiguration cfg,
        ILogger<ReceiptController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /// <summary>
    /// The receipt as PDF bytes, rebuilt from the row on every request --
    /// exactly like GET /sales/invoices/{id}/pdf, so neither Print button
    /// depends on Cloudinary. It is not stored: the slip is a print-out, the
    /// A4 bill is the document of record.
    ///
    /// WHO MAY ASK:
    ///   * perm:invoices.view, same as the A4 bill.
    ///   * A salesperson only for invoices they cut or orders they own -- the
    ///     MaySeeInvoice rule from SalesController, copied below.
    ///   * NEVER the order desk. The owner, 26 September: the order desk has
    ///     "no money ... anywhere". A receipt is nothing but money, so unlike
    ///     the invoice screens (which answer the order desk with figures
    ///     zeroed) there is nothing left to show once the money is taken out,
    ///     and it is refused outright. By ROLE, so a permission ticked in
    ///     Setup cannot undo it.
    /// </summary>
    [HttpGet("invoices/{id:int}/receipt")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> Receipt(int id)
    {
        try
        {
            if (CurrentRole() == OrderWorkflow.RoleOrderDept)
                return new ObjectResult(new { message = "The order desk does not print bills." }) { StatusCode = 403 };

            if (!await MaySeeInvoice(id))
                return new ObjectResult(new { message = "This invoice was not created by you." }) { StatusCode = 403 };

            var data = await ReceiptData(id);
            if (data is null) return NotFound(new { message = $"No invoice with id {id}." });

            Response.Headers.ContentDisposition = $"inline; filename=\"{data.InvoiceNo}-receipt.pdf\"";
            return File(ReceiptPdf.Render(data), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"print the receipt for invoice {id}");
        }
    }

    /* SalesController.MaySeeInvoice, restated: a salesperson sees THEIR OWN
       work -- the invoices they cut and the orders they are the rep on --
       and everyone else (accounts, the owner) sees the whole book. If that
       rule changes there, it must change here too. */
    private async Task<bool> MaySeeInvoice(int invoiceId)
    {
        if (CurrentRole() != OrderWorkflow.RoleSales) return true;
        var me = CurrentUserId();

        return await _db.SalesInvoices.AsNoTracking().AnyAsync(i =>
            i.InvoiceId == invoiceId &&
            (i.CreatedByUserId == me ||
             (i.Order != null && i.Order.SalesPersonUserId == me)));
    }

    /// <summary>
    /// What the slip prints, read the same way SalesController.BillData reads
    /// the A4 bill -- same customer-name rule for walk-ins, same "salesman is
    /// whoever wrote the order", same paid figure -- so the two documents for
    /// one invoice can never disagree on a number. Only the fields the slip
    /// uses are fetched.
    /// </summary>
    private async Task<ReceiptPdf.Data?> ReceiptData(int invoiceId)
    {
        var i = await _db.SalesInvoices.AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .Select(x => new
            {
                x.InvoiceNo,
                x.InvoiceDate,
                orderNo = x.Order != null ? x.Order.OrderNo : null,
                paymentMethod = x.Method.MethodKey,
                statusName = x.Status.StatusName,
                customerName = x.IsWalkIn && x.WalkInName != null ? x.WalkInName : (x.CustomerUser.DisplayName ?? x.CustomerUser.LegalName),
                customerCity = x.IsWalkIn ? null : x.CustomerUser.City.CityName,
                customerPhone = x.IsWalkIn ? x.WalkInPhone : x.CustomerUser.User.Phone,
                x.Subtotal,
                x.DiscountAmount,
                x.TaxAmount,
                x.TotalAmount,
                salesman = x.Order != null ? x.Order.CreatedByUser.FullName : x.CreatedByUser.FullName,
                paid = x.VoucherAllocations
                    .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                    .Sum(v => (decimal?)v.Amount) ?? 0m,
                lines = x.SalesInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    name = l.Product.ProductName,
                    qty = l.Quantity,
                    rate = l.UnitPrice,
                    lineTotal = l.LineTotal
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (i is null) return null;

        var c = await _db.Companies.AsNoTracking()
            .Select(x => new { x.CompanyName, x.Phone, city = x.City.CityName, x.CurrencySymbol })
            .FirstOrDefaultAsync();

        /* A counter sale is settled the moment it is rung up and carries no
           voucher allocation -- the PAID status is what says it was paid.
           Same rule as BillData. */
        var paid = i.paid;
        if (paid == 0 && i.statusName.Equals("Paid", StringComparison.OrdinalIgnoreCase))
            paid = i.TotalAmount;

        return new ReceiptPdf.Data(
            CompanyName: c?.CompanyName ?? "AdvPOS",
            CompanyPhone: c?.Phone,
            CompanyCity: c?.city,
            CurrencySymbol: c?.CurrencySymbol ?? "PKR",
            InvoiceNo: i.InvoiceNo,
            InvoiceDate: i.InvoiceDate,
            OrderNo: i.orderNo,
            CustomerName: i.customerName,
            CustomerPhone: i.customerPhone,
            CustomerCity: i.customerCity,
            Salesman: i.salesman,
            PaymentMethod: i.paymentMethod,
            Subtotal: i.Subtotal,
            Discount: i.DiscountAmount,
            Tax: i.TaxAmount,
            Total: i.TotalAmount,
            Paid: paid,
            Balance: i.TotalAmount - paid,
            Lines: i.lines.Select(l => new ReceiptPdf.Line(l.name, l.qty, l.rate, l.lineTotal)).ToList(),
            PrintedAt: Now());
    }
}
