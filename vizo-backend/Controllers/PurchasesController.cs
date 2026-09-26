using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /purchases screens.
///
/// REBUILT 26 SEP 2026 around the owner's own words:
///
///   * "hamein kisi bhi status ki koi need nahi hai purchase order mein ...
///     jaise hi purchase order create hoga, toh jis bhi location mein yeh
///     purchase order create hua hai us location ke andar automatically items
///     add ho jayenge ... koi GRN wali cheez nahi ... purchase invoice ban jayegi."
///     So a purchase order is written ONCE and in that one transaction the
///     stock lands at its location, the supplier's bill (purchase invoice) is
///     raised and the journal vouchers are posted. There is no status, no
///     approval, no expected date and no goods receipt any more. Old GRNs and
///     bills stay readable.
///
///   * Five price boxes per line -- Cost, Duty, Fi Sabilillah, Margin 1,
///     Margin 2 -- saved on the line exactly as entered ("as it is purchase
///     order ke record mein ja ke save ho jayengi"), each extra one with the
///     reason for it, which is printed on its voucher.
///
///   * Duty is owed to a LOGISTICS COMPANY -- an account under 2150 that the
///     admin adds, renames and deletes right here.
///
///   * Each line becomes a stock LOT (<see cref="StockBatches"/>), so what is
///     left of every purchase is always known. The new selling price is NOT
///     set automatically: the admin sees every earlier lot with its price and
///     what is left of it, ticks the ones to average with the new purchase,
///     and confirms the final price. That decision arrives on each line as
///     <see cref="PriceDecision"/>.
///
///   * Only the Super Admin sees any of it ("koi bhi item kitne mein khareeda
///     hai ... kisi bhi role ko nahi dikhni chahiye"). The accountant keeps
///     what suppliers are owed -- see SupplierPayablesController.
///
/// TRAP: on the purchase side CreatedByUser / ReceivedByUser are EMPLOYEE
/// navigations, not User -- the name is at .CreatedByUser.User.FullName.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/purchases")]
[ApiController]
[Authorize(Roles = "super-admin")]
[Authorize(Policy = "BackOffice")]
public class PurchasesController : ApiControllerBase
{
    private readonly PushNotificationService _push;
    private readonly IServiceScopeFactory _scopes;

    /* The accounts a purchase posts to. By code, like the rest of this
       codebase (AccountingController looks up 1130/2101 the same way). */
    private const string InventoryCode = "1140";
    private const string PayablesCode = "2101";
    private const string LogisticsGroupCode = "2150";
    private const string FsReserveCode = "2160";
    private const string Margin1ReserveCode = "2161";
    private const string Margin2ReserveCode = "2162";

    public PurchasesController(AppDbContext db, IConfiguration cfg,
        ILogger<PurchasesController> logger, IWebHostEnvironment env,
        PushNotificationService push, IServiceScopeFactory scopes)
        : base(db, cfg, logger, env)
    {
        _push = push;
        _scopes = scopes;
    }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE ORDERS -- read
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("orders")]
    public async Task<IActionResult> GetPurchaseOrders(
        [FromQuery] string? q, [FromQuery] int? supplierId, [FromQuery] int? locationId)
    {
        try
        {
            var rows = _db.PurchaseOrders.AsNoTracking().AsQueryable();

            if (supplierId is not null) rows = rows.Where(p => p.SupplierUserId == supplierId);
            if (locationId is not null) rows = rows.Where(p => p.LocationId == locationId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p => p.PoNo.ToLower().Contains(term) ||
                                       (p.SupplierUser.DisplayName ?? p.SupplierUser.LegalName).ToLower().Contains(term) ||
                                       p.PurchaseOrderItems.Any(i => i.Product.ProductName.ToLower().Contains(term)));
            }

            var items = await rows
                .OrderByDescending(p => p.PoDate).ThenByDescending(p => p.PoId)
                .Select(p => new
                {
                    id = p.PoId,
                    poNo = p.PoNo,
                    supplierId = p.SupplierUserId,
                    supplierName = (p.SupplierUser.DisplayName ?? p.SupplierUser.LegalName),
                    locationId = p.LocationId,
                    location = p.Location.LocationName,
                    poDate = p.PoDate,
                    itemCount = p.PurchaseOrderItems.Count,
                    units = p.PurchaseOrderItems.Sum(i => (int?)i.Quantity) ?? 0,
                    total = p.TotalAmount,
                    saleValue = p.PurchaseOrderItems.Sum(i => (decimal?)(i.Quantity *
                        (i.UnitCost + i.DutyPrice + i.FsPrice + i.Margin1Price + i.Margin2Price))) ?? 0m,
                    invoiceId = p.PurchaseInvoices.Select(i => (int?)i.PiId).FirstOrDefault(),
                    invoiceNo = p.PurchaseInvoices.Select(i => i.InvoiceNo).FirstOrDefault(),
                    createdBy = p.CreatedByUser.User.FullName,
                    notes = p.Notes
                })
                .ToListAsync();

            return Ok(items.Select(p => new
            {
                p.id, p.poNo, p.supplierId, p.supplierName,
                supplierInitials = Initials(p.supplierName),
                p.locationId, p.location, p.poDate, p.itemCount, p.units,
                p.total, p.saleValue, p.invoiceId, p.invoiceNo, p.createdBy, p.notes
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-order list");
        }
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetPurchaseOrder(int id)
    {
        try
        {
            var p = await _db.PurchaseOrders.AsNoTracking()
                .Where(x => x.PoId == id)
                .Select(x => new
                {
                    id = x.PoId,
                    poNo = x.PoNo,
                    supplierId = x.SupplierUserId,
                    supplierName = (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName),
                    supplierCode = x.SupplierUser.PartyCode,
                    supplierPhone = x.SupplierUser.User.Phone,
                    supplierBillNo = x.SupplierBillNo,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    poDate = x.PoDate,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    total = x.TotalAmount,
                    notes = x.Notes,
                    createdBy = x.CreatedByUser.User.FullName,
                    lines = x.PurchaseOrderItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.PoItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        imageUrl = i.Product.ImageUrl,
                        name = i.Product.ProductName,
                        qty = i.Quantity,
                        unitCost = i.UnitCost,
                        dutyPrice = i.DutyPrice,
                        dutyAccountId = i.DutyAccountId,
                        dutyAccount = i.DutyAccount != null ? i.DutyAccount.AccountName : null,
                        fsPrice = i.FsPrice,
                        margin1Price = i.Margin1Price,
                        margin2Price = i.Margin2Price,
                        dutyNote = i.DutyNote,
                        fsNote = i.FsNote,
                        margin1Note = i.Margin1Note,
                        margin2Note = i.Margin2Note,
                        unitSalePrice = i.UnitCost + i.DutyPrice + i.FsPrice + i.Margin1Price + i.Margin2Price,
                        lineTotal = i.LineTotal,
                        /* What is left of THIS purchase today, everywhere. */
                        onHand = _db.StockBatchBalances
                            .Where(b => b.Batch.PoItemId == i.PoItemId)
                            .Sum(b => (int?)b.Quantity) ?? 0
                    }).ToList(),
                    invoice = x.PurchaseInvoices.Select(i => new { id = i.PiId, no = i.InvoiceNo }).FirstOrDefault(),
                    vouchers = x.PurchaseOrderEntries
                        .OrderBy(e => e.EntryId)
                        .Select(e => new
                        {
                            id = e.EntryId,
                            entryNo = e.Entry.EntryNo,
                            component = e.Component,
                            narration = e.Entry.Narration,
                            amount = e.Entry.JournalEntryLines.Sum(l => (decimal?)l.DebitAmount) ?? 0m
                        }).ToList(),
                    /* Receipts made the old way, before 26 Sep. Read-only history. */
                    receipts = x.GoodsReceipts.OrderByDescending(g => g.ReceiptDate).Select(g => new
                    {
                        id = g.GrnId,
                        grnNo = g.GrnNo,
                        receiptDate = g.ReceiptDate
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No purchase order with id {id}." });

            return Ok(new
            {
                p.id, p.poNo, p.supplierId, p.supplierName,
                supplierInitials = Initials(p.supplierName),
                p.supplierCode, p.supplierPhone, p.supplierBillNo, p.locationId, p.location,
                p.poDate, p.subtotal, p.discount, p.total, p.notes, p.createdBy,
                p.lines, p.invoice, p.vouchers, p.receipts,
                totals = new
                {
                    goods = p.lines.Sum(l => l.qty * l.unitCost),
                    duty = p.lines.Sum(l => l.qty * l.dutyPrice),
                    fs = p.lines.Sum(l => l.qty * l.fsPrice),
                    margin1 = p.lines.Sum(l => l.qty * l.margin1Price),
                    margin2 = p.lines.Sum(l => l.qty * l.margin2Price),
                    saleValue = p.lines.Sum(l => l.qty * l.unitSalePrice)
                }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE AVERAGING POPUP
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every earlier lot of one product, with its price parts and what is left
    /// of it -- the list the admin ticks to average a new purchase against.
    ///
    /// "On hand" counts every place EXCEPT a claim location: damaged stock in
    /// Claim Stock is not going to be sold at any price, so it must not pull
    /// the average towards its own. Lots with nothing left are still listed
    /// (the owner asked to see "tamam previous selling prices") but they weigh
    /// nothing in a quantity-weighted average.
    /// </summary>
    [HttpGet("products/{productId:int}/lots")]
    public async Task<IActionResult> GetProductLots(int productId)
    {
        try
        {
            var product = await _db.Products.AsNoTracking()
                .Where(p => p.ProductId == productId)
                .Select(p => new
                {
                    id = p.ProductId, name = p.ProductName, sku = p.Sku, imageUrl = p.ImageUrl,
                    costPrice = p.CostPrice, dutyPrice = p.DutyPrice, fsPrice = p.FsPrice,
                    margin1Price = p.MarginPrice, margin2Price = p.Margin2Price, salePrice = p.SalePrice
                })
                .FirstOrDefaultAsync();
            if (product is null) return NotFound(new { message = $"No product with id {productId}." });

            var lots = await _db.StockBatches.AsNoTracking()
                .Where(b => b.ProductId == productId)
                .OrderByDescending(b => b.BatchDate).ThenByDescending(b => b.BatchId)
                .Select(b => new
                {
                    id = b.BatchId,
                    batchNo = b.BatchNo,
                    date = b.BatchDate,
                    poId = b.PoItem != null ? (int?)b.PoItem.PoId : null,
                    supplier = b.PoItem != null
                        ? (b.PoItem.Po.SupplierUser.DisplayName ?? b.PoItem.Po.SupplierUser.LegalName)
                        : null,
                    qtyReceived = b.QtyReceived,
                    unitCost = b.UnitCost, unitDuty = b.UnitDuty, unitFs = b.UnitFs,
                    unitMargin1 = b.UnitMargin1, unitMargin2 = b.UnitMargin2,
                    unitSalePrice = b.UnitCost + b.UnitDuty + b.UnitFs + b.UnitMargin1 + b.UnitMargin2,
                    onHand = b.Balances.Where(x => x.Location.Kind.KindKey != "claim").Sum(x => (int?)x.Quantity) ?? 0,
                    inClaim = b.Balances.Where(x => x.Location.Kind.KindKey == "claim").Sum(x => (int?)x.Quantity) ?? 0,
                    places = b.Balances.Where(x => x.Quantity != 0).Select(x => new
                    {
                        location = x.Location.LocationName, qty = x.Quantity
                    }).ToList()
                })
                .ToListAsync();

            return Ok(new
            {
                product,
                lots = lots.Select(l => new
                {
                    l.id, l.batchNo, l.date, l.poId, l.supplier, l.qtyReceived,
                    l.unitCost, l.unitDuty, l.unitFs, l.unitMargin1, l.unitMargin2, l.unitSalePrice,
                    onHand = Math.Max(0, l.onHand), l.inClaim, l.places
                }),
                onHandTotal = lots.Sum(l => Math.Max(0, l.onHand))
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the lots of product {productId}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOGISTICS COMPANIES -- accounts under 2150
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The owner: "duty directly logistic companies ko effect karen gi ... ye
    /// bhi as a account hongi ... ye logistics add create bhi kar sakta hai".
    /// A logistics company IS an account -- a child of 2150 Logistics Companies
    /// -- so what is owed to each shows up in the ledger, trial balance and
    /// balance sheet with no second list to keep in step.
    /// </summary>
    [HttpGet("logistics")]
    public async Task<IActionResult> GetLogistics([FromQuery] bool includeInactive = false)
    {
        try
        {
            var group = await LogisticsGroupAsync();
            var rows = await _db.Accounts.AsNoTracking()
                .Where(a => a.ParentAccountId == group.AccountId && (includeInactive || a.IsActive))
                .OrderBy(a => a.AccountName)
                .Select(a => new
                {
                    id = a.AccountId, code = a.AccountCode, name = a.AccountName, isActive = a.IsActive,
                    /* What we owe them now: duty credited less anything paid. */
                    balance = _db.JournalEntryLines
                        .Where(l => l.AccountId == a.AccountId && l.Entry.Status.StatusKey == "POSTED")
                        .Sum(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m,
                    used = _db.JournalEntryLines.Any(l => l.AccountId == a.AccountId)
                           || _db.PurchaseOrderItems.Any(i => i.DutyAccountId == a.AccountId)
                })
                .ToListAsync();
            return Ok(rows);
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the logistics companies");
        }
    }

    [HttpPost("logistics")]
    public async Task<IActionResult> CreateLogistics([FromBody] LogisticsRequest body)
    {
        try
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 100)
                return BadRequest(new { message = "Give the company a name of 2 to 100 characters." });

            var group = await LogisticsGroupAsync();
            if (await _db.Accounts.AnyAsync(a => a.ParentAccountId == group.AccountId && a.AccountName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"\"{name}\" is already a logistics company." });

            /* Next free code under the group: 2151, 2152 ... 2159, then 21510 ...
               The code is the account's identity in reports, so it is never
               reused even after a delete. */
            var prefix = LogisticsGroupCode[..3];            // "215": 2151 ... 2159, then 21510 ...
            var codes = await _db.Accounts.Where(a => a.AccountCode.StartsWith(prefix) && a.AccountCode != LogisticsGroupCode)
                .Select(a => a.AccountCode).ToListAsync();
            var next = 1;
            while (codes.Contains($"{LogisticsGroupCode[..3]}{next}") || codes.Contains($"{LogisticsGroupCode}{next}")) next++;
            var code = next <= 9 ? $"{LogisticsGroupCode[..3]}{next}" : $"{LogisticsGroupCode}{next}";

            var acc = new Account
            {
                AccountCode = code,
                AccountName = name,
                ParentAccountId = group.AccountId,
                AccountTypeId = group.AccountTypeId,
                IsGroup = false,
                OpeningBalance = 0,
                CurrencyCode = "PKR",
                IsActive = true
            };
            _db.Accounts.Add(acc);
            await _db.SaveChangesAsync();
            await Log("LOGISTICS_ADDED", "Account", acc.AccountCode, name, 1);
            return Ok(new { id = acc.AccountId, code = acc.AccountCode, name = acc.AccountName, message = $"{name} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "add the logistics company");
        }
    }

    [HttpPut("logistics/{id:int}")]
    public async Task<IActionResult> UpdateLogistics(int id, [FromBody] LogisticsRequest body)
    {
        try
        {
            var group = await LogisticsGroupAsync();
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.AccountId == id && a.ParentAccountId == group.AccountId);
            if (acc is null) return NotFound(new { message = "No such logistics company." });

            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 100)
                return BadRequest(new { message = "Give the company a name of 2 to 100 characters." });
            if (await _db.Accounts.AnyAsync(a => a.AccountId != id && a.ParentAccountId == group.AccountId && a.AccountName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"\"{name}\" is already a logistics company." });

            acc.AccountName = name;
            if (body.IsActive is not null) acc.IsActive = body.IsActive.Value;
            await _db.SaveChangesAsync();
            await Log("LOGISTICS_EDITED", "Account", acc.AccountCode, name, 1);
            return Ok(new { id, message = $"{name} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the logistics company");
        }
    }

    /// <summary>
    /// Deletes a logistics company that has never been used. One that duty has
    /// been posted to cannot vanish -- the ledger would lose what we owe them --
    /// so it is switched off instead (it leaves the dropdown, keeps its history).
    /// </summary>
    [HttpDelete("logistics/{id:int}")]
    public async Task<IActionResult> DeleteLogistics(int id)
    {
        try
        {
            var group = await LogisticsGroupAsync();
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.AccountId == id && a.ParentAccountId == group.AccountId);
            if (acc is null) return NotFound(new { message = "No such logistics company." });

            var used = await _db.JournalEntryLines.AnyAsync(l => l.AccountId == id)
                       || await _db.PurchaseOrderItems.AnyAsync(i => i.DutyAccountId == id)
                       || await _db.Expenses.AnyAsync(e => e.PaidFromAccountId == id || e.ExpenseAccountId == id)
                       || await _db.Vouchers.AnyAsync(v => v.CashBankAccountId == id);
            if (used)
            {
                acc.IsActive = false;
                await _db.SaveChangesAsync();
                await Log("LOGISTICS_DEACTIVATED", "Account", acc.AccountCode, acc.AccountName, 2);
                return Ok(new
                {
                    id, deactivated = true,
                    message = $"{acc.AccountName} has duty on its account, so it was switched off instead of deleted. Its history stays."
                });
            }

            _db.Accounts.Remove(acc);
            await _db.SaveChangesAsync();
            await Log("LOGISTICS_DELETED", "Account", acc.AccountCode, acc.AccountName, 2);
            return Ok(new { id, deactivated = false, message = $"{acc.AccountName} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "delete the logistics company");
        }
    }

    private async Task<Account> LogisticsGroupAsync() =>
        await _db.Accounts.FirstOrDefaultAsync(a => a.AccountCode == LogisticsGroupCode)
        ?? throw new InvalidOperationException(
            "Account 2150 Logistics Companies is missing -- run backend/database/26_purchase_pricing_and_batches.sql.");

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            var group = await LogisticsGroupAsync();
            return Ok(new
            {
                suppliers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 6 || p.User.RoleId == 7) && p.User.IsActive)
                    .OrderBy(p => (p.DisplayName ?? p.LegalName))
                    .Select(p => new { id = p.UserId, code = p.PartyCode, name = (p.DisplayName ?? p.LegalName) })
                    .ToListAsync(),
                /* Claim Stock is not a place goods are bought INTO -- the owner:
                   "receiving location ke andar 'Claim Stock' nahi chahiye". Every
                   location of the claim kind is left out, not the one by name. */
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive && l.Kind.KindKey != "claim")
                    .OrderByDescending(l => l.IsDefault).ThenBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName, kind = l.Kind.KindName })
                    .ToListAsync(),
                logistics = await _db.Accounts.AsNoTracking()
                    .Where(a => a.ParentAccountId == group.AccountId && a.IsActive)
                    .OrderBy(a => a.AccountName)
                    .Select(a => new { id = a.AccountId, code = a.AccountCode, name = a.AccountName })
                    .ToListAsync(),
                /* Each product with its current five price parts: the PO line
                   opens pre-filled with them ("by default jo bhi database mein
                   stored hongi, woh uth kar ke ismein aa jayengi"). */
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive).OrderBy(p => p.ProductName)
                    .Select(p => new
                    {
                        id = p.ProductId, sku = p.Sku, name = p.ProductName, imageUrl = p.ImageUrl,
                        packing = p.Packing,
                        costPrice = p.CostPrice, dutyPrice = p.DutyPrice, fsPrice = p.FsPrice,
                        margin1Price = p.MarginPrice, margin2Price = p.Margin2Price, salePrice = p.SalePrice
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load purchase lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREATE -- one transaction: order, stock, lots, bill, vouchers, price
    // ══════════════════════════════════════════════════════════════════

    [HttpPost("orders")]
    public async Task<IActionResult> CreatePurchaseOrder([FromBody] PoRequest body)
    {
        try
        {
            /* ── validate everything before anything is written ── */
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "Add at least one item." });
            if (body.Lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
                return BadRequest(new { message = "An item is on the order twice. Put its whole quantity on one line." });
            if (body.Discount < 0)
                return BadRequest(new { message = "The discount cannot be negative." });

            var supplier = await _db.Parties.AsNoTracking()
                .Where(p => p.UserId == body.SupplierId && (p.User.RoleId == 6 || p.User.RoleId == 7))
                .Select(p => new { p.UserId, name = p.DisplayName ?? p.LegalName })
                .FirstOrDefaultAsync();
            if (supplier is null) return BadRequest(new { message = "Pick a supplier." });

            var location = await _db.Locations.AsNoTracking()
                .Where(l => l.LocationId == body.LocationId && l.IsActive)
                .Select(l => new { l.LocationId, l.LocationName, kind = l.Kind.KindKey })
                .FirstOrDefaultAsync();
            if (location is null) return BadRequest(new { message = "Pick where the goods are received." });
            if (location.kind == "claim")
                return BadRequest(new { message = $"{location.LocationName} is a claim location. Goods are not bought into it." });

            var group = await LogisticsGroupAsync();
            var logistics = await _db.Accounts.AsNoTracking()
                .Where(a => a.ParentAccountId == group.AccountId && a.IsActive && !a.IsGroup)
                .Select(a => new { a.AccountId, a.AccountName }).ToListAsync();

            var productIds = body.Lines.Select(l => l.ProductId).ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.ProductId)).ToListAsync();

            foreach (var l in body.Lines)
            {
                var p = products.FirstOrDefault(x => x.ProductId == l.ProductId);
                if (p is null) return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
                if (l.Qty <= 0) return BadRequest(new { message = $"{p.ProductName}: the quantity must be above zero." });
                if (l.UnitCost < 0 || l.DutyPrice < 0 || l.FsPrice < 0 || l.Margin1Price < 0 || l.Margin2Price < 0)
                    return BadRequest(new { message = $"{p.ProductName}: no price box can be negative." });
                if (l.DutyPrice > 0 && (l.DutyAccountId is null || logistics.All(a => a.AccountId != l.DutyAccountId)))
                    return BadRequest(new { message = $"{p.ProductName}: pick the logistics company the duty is paid to." });
                if (l.Pricing is null)
                    return BadRequest(new { message = $"{p.ProductName}: decide its new selling price before saving." });
                if (!l.Pricing.Keep && l.Pricing.FinalSalePrice is < 0)
                    return BadRequest(new { message = $"{p.ProductName}: the selling price cannot be negative." });
            }

            if (body.Discount > body.Lines.Sum(l => l.Qty * l.UnitCost))
                return BadRequest(new { message = "The discount is more than the goods are worth." });

            var date = body.PoDate ?? Today();
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
            if (period is null)
                return BadRequest(new { message = $"No accounting period covers {date:dd MMM yyyy}, so its vouchers cannot be posted." });
            if (period.IsClosed)
                return BadRequest(new { message = $"{period.PeriodName} is closed. Pick a date in an open period." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can raise a purchase order." });

            var acc = await _db.Accounts.AsNoTracking()
                .Where(a => new[] { InventoryCode, PayablesCode, FsReserveCode, Margin1ReserveCode, Margin2ReserveCode }.Contains(a.AccountCode))
                .ToDictionaryAsync(a => a.AccountCode, a => a.AccountId);
            foreach (var code in new[] { InventoryCode, PayablesCode, FsReserveCode, Margin1ReserveCode, Margin2ReserveCode })
                if (!acc.ContainsKey(code))
                    return BadRequest(new { message = $"Account {code} is missing -- run migration 26." });

            var posted = await _db.PostingStatuses.FirstAsync(s => s.StatusKey == "POSTED");
            var purchaseType = await _db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "PURCHASE")
                               ?? await _db.JournalEntryTypes.FirstAsync(t => t.TypeKey == "JOURNAL");
            var issued = await _db.InvoiceStatuses.FirstAsync(s => s.StatusKey == "ISSUED");
            var credit = await _db.PaymentMethods.FirstOrDefaultAsync(m => m.MethodKey == "CREDIT")
                         ?? await _db.PaymentMethods.FirstAsync();
            var stockIn = await _db.MovementTypes.FirstAsync(m => m.TypeKey == "PURCHASE");

            /* The lots each line will average with, and how much is left of each
               (not counting claim locations), read BEFORE this order adds to them. */
            var chosenIds = body.Lines.Where(l => !l.Pricing!.Keep)
                .SelectMany(l => l.Pricing!.BatchIds ?? new List<int>()).Distinct().ToList();
            var chosen = await _db.StockBatches.AsNoTracking()
                .Where(b => chosenIds.Contains(b.BatchId))
                .Select(b => new
                {
                    b.BatchId, b.ProductId, b.UnitCost, b.UnitDuty, b.UnitFs, b.UnitMargin1, b.UnitMargin2,
                    onHand = b.Balances.Where(x => x.Location.Kind.KindKey != "claim").Sum(x => (int?)x.Quantity) ?? 0
                })
                .ToListAsync();
            foreach (var l in body.Lines.Where(l => !l.Pricing!.Keep))
                foreach (var bid in l.Pricing!.BatchIds ?? new List<int>())
                    if (chosen.All(c => c.BatchId != bid || c.ProductId != l.ProductId))
                        return BadRequest(new { message = "One of the ticked earlier purchases does not belong to that item. Reopen the price popup." });

            /* ── write ── */
            await using var tx = await _db.Database.BeginTransactionAsync();

            var subtotal = body.Lines.Sum(l => l.Qty * l.UnitCost);
            var po = new PurchaseOrder
            {
                PoNo = await NextNumber("PO"),
                SupplierUserId = supplier.UserId,
                LocationId = location.LocationId,
                PoDate = date,
                Subtotal = subtotal,
                DiscountAmount = body.Discount,
                TaxAmount = 0,
                TotalAmount = subtotal - body.Discount,
                Notes = Clean(body.Notes, 500),
                SupplierBillNo = Clean(body.SupplierBillNo, 50),
                CreatedByUserId = me.Value
            };
            _db.PurchaseOrders.Add(po);

            var now = Now();
            short n = 1;
            var lines = new List<PurchaseOrderItem>();
            foreach (var l in body.Lines)
            {
                var line = new PurchaseOrderItem
                {
                    Po = po,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitCost = l.UnitCost,
                    TaxPercent = 0,
                    LineTotal = l.Qty * l.UnitCost,
                    DutyPrice = l.DutyPrice,
                    DutyAccountId = l.DutyPrice > 0 ? l.DutyAccountId : null,
                    FsPrice = l.FsPrice,
                    Margin1Price = l.Margin1Price,
                    Margin2Price = l.Margin2Price,
                    DutyNote = Clean(l.DutyNote, 300),
                    FsNote = Clean(l.FsNote, 300),
                    Margin1Note = Clean(l.Margin1Note, 300),
                    Margin2Note = Clean(l.Margin2Note, 300)
                };
                _db.PurchaseOrderItems.Add(line);
                lines.Add(line);

                /* The stock lands NOW, at the order's location, as a new lot. */
                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == location.LocationId);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = location.LocationId, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                }
                bal.Quantity += l.Qty;

                var lot = StockBatches.NewLot(_db, line, po.PoNo, date, now);
                await StockBatches.RecordAsync(_db, new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = location.LocationId,
                    MovementTypeId = stockIn.MovementTypeId,
                    MovedAt = now,
                    ReferenceNo = po.PoNo,
                    Quantity = l.Qty,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                }, into: lot);

                /* The new selling price -- only if the admin chose one. */
                if (!l.Pricing!.Keep)
                {
                    var p = products.First(x => x.ProductId == l.ProductId);
                    var picks = chosen.Where(c => (l.Pricing.BatchIds ?? new()).Contains(c.BatchId))
                        .Select(c => (qty: Math.Max(0, c.onHand), cost: c.UnitCost, duty: c.UnitDuty,
                                      fs: c.UnitFs, m1: c.UnitMargin1, m2: c.UnitMargin2))
                        .Append((qty: l.Qty, cost: l.UnitCost, duty: l.DutyPrice,
                                 fs: l.FsPrice, m1: l.Margin1Price, m2: l.Margin2Price))
                        .ToList();
                    var units = picks.Sum(x => x.qty);
                    decimal Avg(Func<(int qty, decimal cost, decimal duty, decimal fs, decimal m1, decimal m2), decimal> part) =>
                        Math.Round(picks.Sum(x => x.qty * part(x)) / units, 2, MidpointRounding.AwayFromZero);

                    var cost = Avg(x => x.cost);
                    var duty = Avg(x => x.duty);
                    var fs = Avg(x => x.fs);
                    var m1 = Avg(x => x.m1);
                    var m2 = Avg(x => x.m2);
                    /* The average SALE price is taken whole, not as the sum of the
                       five rounded parts: 10 x 1,400 + 50 x 2,000 over 60 is 1,900
                       exactly, where the rounded parts add to 1,899.99. */
                    var average = Avg(x => x.cost + x.duty + x.fs + x.m1 + x.m2);
                    var final = Math.Round(l.Pricing.FinalSalePrice ?? average, 2, MidpointRounding.AwayFromZero);

                    /* The parts must still add up to the price the admin settled
                       on. Rounding paisa, or his own figure (1,900 -> 1,950): the
                       difference is margin, so it goes into Margin 1, the ordinary
                       margin, and the other four parts stay the true averages. */
                    p.CostPrice = cost;
                    p.DutyPrice = duty;
                    p.FsPrice = fs;
                    p.MarginPrice = m1 + (final - (cost + duty + fs + m1 + m2));
                    p.Margin2Price = m2;
                    p.SalePrice = final;
                }
            }
            await _db.SaveChangesAsync();

            /* ── the supplier's bill ── */
            var pi = new PurchaseInvoice
            {
                InvoiceNo = await NextNumber("PI"),
                SupplierInvoiceNo = po.SupplierBillNo ?? po.PoNo,
                SupplierUserId = supplier.UserId,
                PoId = po.PoId,
                InvoiceDate = date,
                DueDate = date.AddDays(30),
                Subtotal = subtotal,
                DiscountAmount = body.Discount,
                TaxAmount = 0,
                WhtAmount = 0,
                TotalAmount = po.TotalAmount,
                StatusId = issued.StatusId,
                MethodId = credit.MethodId,
                CreatedByUserId = me.Value
            };
            _db.PurchaseInvoices.Add(pi);
            short k = 1;
            foreach (var line in lines)
                _db.PurchaseInvoiceItems.Add(new PurchaseInvoiceItem
                {
                    Pi = pi, LineNo = k++, ProductId = line.ProductId, Quantity = line.Quantity,
                    UnitCost = line.UnitCost, TaxPercent = 0, LineTotal = line.LineTotal
                });

            /* ── the vouchers: one per price part, each its own JV ──
               The owner: "sab ka alag alag ban ke aaye, har purchase order ki
               apni JV ho". Every one debits Inventory, line by line, so the
               stock is carried at its full selling value, and credits whoever
               that part is owed to. The "overall JV" he also asked for is the
               printable summary of all of them (document kind
               "purchase-vouchers"), not a sixth posting -- posting the same
               amounts twice would double the books. */
            var names = products.ToDictionary(p => p.ProductId, p => p.ProductName);
            string Desc(PurchaseOrderItem l, decimal unit, string? note) =>
                Clean($"{names[l.ProductId]} × {l.Quantity} @ {unit:N2}" + (string.IsNullOrWhiteSpace(note) ? "" : $" — {note}"), 300)!;

            var entries = new List<JournalEntry>();

            // GOODS -- Dr Inventory / Cr the supplier (Accounts Payable)
            var goods = lines.Select(l => new JvLine(acc[InventoryCode], null, Desc(l, l.UnitCost, null), l.Quantity * l.UnitCost, 0)).ToList();
            if (body.Discount > 0)
                goods.Add(new JvLine(acc[InventoryCode], null, $"Discount on {po.PoNo}", 0, body.Discount));
            goods.Add(new JvLine(acc[PayablesCode], supplier.UserId, $"{supplier.name} — bill {pi.SupplierInvoiceNo}", 0, po.TotalAmount));
            var goodsJv = await AddJvAsync(po, "GOODS", $"Goods from {supplier.name}", date, period.PeriodId, purchaseType.EntryTypeId, posted.StatusId, goods);
            if (goodsJv is not null) { entries.Add(goodsJv); pi.Entry = goodsJv; }

            // DUTY -- Dr Inventory / Cr each logistics company
            var dutyLines = lines.Where(l => l.DutyPrice > 0).ToList();
            if (dutyLines.Count > 0)
            {
                var dl = dutyLines.Select(l => new JvLine(acc[InventoryCode], null, Desc(l, l.DutyPrice, l.DutyNote), l.Quantity * l.DutyPrice, 0)).ToList();
                foreach (var g in dutyLines.GroupBy(l => l.DutyAccountId!.Value))
                    dl.Add(new JvLine(g.Key, null,
                        $"Duty owed to {logistics.First(a => a.AccountId == g.Key).AccountName}",
                        0, g.Sum(l => l.Quantity * l.DutyPrice)));
                entries.Add((await AddJvAsync(po, "DUTY", "Duty", date, period.PeriodId, purchaseType.EntryTypeId, posted.StatusId, dl))!);
            }

            // FS, MARGIN 1, MARGIN 2 -- Dr Inventory / Cr their reserve
            foreach (var (key, title, code, unit, note) in new (string, string, string, Func<PurchaseOrderItem, decimal>, Func<PurchaseOrderItem, string?>)[]
            {
                ("FS", "Fi Sabilillah", FsReserveCode, l => l.FsPrice, l => l.FsNote),
                ("MARGIN1", "Margin 1", Margin1ReserveCode, l => l.Margin1Price, l => l.Margin1Note),
                ("MARGIN2", "Margin 2", Margin2ReserveCode, l => l.Margin2Price, l => l.Margin2Note),
            })
            {
                var part = lines.Where(l => unit(l) > 0).ToList();
                if (part.Count == 0) continue;
                var jl = part.Select(l => new JvLine(acc[InventoryCode], null, Desc(l, unit(l), note(l)), l.Quantity * unit(l), 0)).ToList();
                jl.Add(new JvLine(acc[code], null, $"{title} on {po.PoNo}", 0, part.Sum(l => l.Quantity * unit(l))));
                entries.Add((await AddJvAsync(po, key, title, date, period.PeriodId, purchaseType.EntryTypeId, posted.StatusId, jl))!);
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PO_CREATED", "PurchaseOrder", po.PoNo,
                $"{lines.Count} lines, {po.TotalAmount:N0}, {entries.Count} vouchers, stock into {location.LocationName}", 1);

            /* PDFs exist the moment the documents do (see DocumentArchive) --
               but one purchase order is up to EIGHT documents (order, bill,
               the overall sheet, five vouchers), and eight Cloudinary uploads
               one after another kept the admin staring at "Saving..." for over
               ten seconds (measured 26 Sep). Speed is a standing requirement
               here (vizo-erp/AGENTS.md), and nothing on the next screen waits
               for a stored file -- Print and Download build one on demand if it
               is not there yet. So they are stored AFTER the response, on a
               scope of their own: the request's DbContext is disposed the
               moment the response is sent. A failure is logged and swallowed,
               exactly as it always was. */
            var uid = CurrentUserId();
            var docs = new List<(string kind, int id)>
            {
                ("purchase-order", po.PoId), ("purchase-invoice", pi.PiId), ("purchase-vouchers", po.PoId)
            };
            docs.AddRange(entries.Select(e => ("journal-entry", e.EntryId)));
            var cfg = _cfg;
            var log = _logger;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    foreach (var (kind, docId) in docs)
                        await DocumentArchive.TryStoreForAsync(db, cfg, log, kind, docId, uid);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Archiving the documents of purchase order {PoId} failed", po.PoId);
                }
            });

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.PoCreated,
                $"Purchase order written by {CurrentUserName()}",
                $"{po.PoNo} -- {supplier.name}, PKR {po.TotalAmount:N0}. Stock is in {location.LocationName}.",
                url: $"/purchases/orders/{po.PoId}",
                exceptUserId: uid);

            /* The accountant pays suppliers: tell them a bill exists -- the
               amount owed, not what anything cost. */
            await _push.NotifyRolesAsync(
                new[] { "accountant" },
                NotificationKinds.PurchaseInvoice,
                $"New supplier bill: {supplier.name}",
                $"{pi.InvoiceNo} -- PKR {pi.TotalAmount:N0}, due {pi.DueDate:dd MMM yyyy}.",
                url: "/parties/suppliers",
                exceptUserId: uid);

            /* The order desk works the shelves: tell them what arrived, never
               at what price. */
            await _push.NotifyRolesAsync(
                new[] { "order-dept" },
                NotificationKinds.GrnCreated,
                $"Stock arrived at {location.LocationName}",
                string.Join(", ", lines.Take(4).Select(l => $"{names[l.ProductId]} × {l.Quantity}"))
                    + (lines.Count > 4 ? $" and {lines.Count - 4} more" : ""),
                url: "/inventory/stock-levels",
                exceptUserId: uid);

            return Ok(new
            {
                id = po.PoId, poNo = po.PoNo, invoiceId = pi.PiId, invoiceNo = pi.InvoiceNo,
                vouchers = entries.Select(e => e.EntryNo),
                message = $"{po.PoNo} saved: stock is in {location.LocationName}, bill {pi.InvoiceNo} raised, {entries.Count} vouchers posted."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the purchase order");
        }
    }

    private sealed record JvLine(int AccountId, int? PartyId, string Description, decimal Debit, decimal Credit);

    /// <summary>Adds one posted journal voucher for one price part of a purchase order.</summary>
    private async Task<JournalEntry?> AddJvAsync(PurchaseOrder po, string component, string title, DateOnly date,
        int periodId, int typeId, int postedId, List<JvLine> lines)
    {
        lines = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (lines.Count < 2) return null;
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            throw new InvalidOperationException($"{po.PoNo} {title} voucher does not balance.");

        var entry = new JournalEntry
        {
            EntryNo = await NextNumber("JV"),
            EntryDate = date,
            EntryTypeId = typeId,
            PeriodId = periodId,
            LocationId = po.LocationId,
            ReferenceNo = po.PoNo,
            Narration = $"{po.PoNo} · {title}",
            StatusId = postedId,
            CreatedByUserId = CurrentUserId(),
            PostedByUserId = CurrentUserId(),
            CreatedAt = Today()
        };
        short n = 1;
        foreach (var l in lines)
            entry.JournalEntryLines.Add(new JournalEntryLine
            {
                LineNo = n++, AccountId = l.AccountId, PartyUserId = l.PartyId,
                Description = l.Description, DebitAmount = l.Debit, CreditAmount = l.Credit
            });
        _db.JournalEntries.Add(entry);
        _db.PurchaseOrderEntries.Add(new PurchaseOrderEntry { Po = po, Entry = entry, Component = component });
        return entry;
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    // ══════════════════════════════════════════════════════════════════
    //  HISTORY -- goods receipts and bills made before 26 Sep, and bills since
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("grns/{id:int}")]
    public async Task<IActionResult> GetGrn(int id)
    {
        try
        {
            var g = await _db.GoodsReceipts.AsNoTracking()
                .Where(x => x.GrnId == id)
                .Select(x => new
                {
                    id = x.GrnId,
                    grnNo = x.GrnNo,
                    poId = x.PoId,
                    poNo = x.Po != null ? x.Po.PoNo : null,
                    supplierId = x.SupplierUserId,
                    supplierName = (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName),
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    receiptDate = x.ReceiptDate,
                    deliveryNoteNo = x.DeliveryNoteNo,
                    vehicleNo = x.VehicleNo,
                    totalValue = x.TotalValue,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    receivedBy = x.ReceivedByUser.User.FullName,
                    notes = x.Notes,
                    lines = x.GoodsReceiptItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.GrnItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        imageUrl = i.Product.ImageUrl,
                        name = i.Product.ProductName,
                        qtyReceived = i.QtyReceived,
                        qtyDamaged = i.QtyDamaged,
                        qtyAccepted = i.QtyReceived - i.QtyDamaged,
                        unitCost = i.UnitCost,
                        batchNo = i.BatchNo,
                        expiryDate = i.ExpiryDate
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (g is null) return NotFound(new { message = $"No goods receipt with id {id}." });

            return Ok(new
            {
                g.id, g.grnNo, g.poId, g.poNo, g.supplierId, g.supplierName,
                supplierInitials = Initials(g.supplierName),
                g.locationId, g.location, g.receiptDate, g.deliveryNoteNo, g.vehicleNo,
                g.totalValue, g.status, g.statusName, g.receivedBy, g.notes, g.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load goods receipt {id}");
        }
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> GetPurchaseInvoices([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.PurchaseInvoices.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(i => i.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term) ||
                                       i.SupplierInvoiceNo.ToLower().Contains(term) ||
                                       (i.SupplierUser.DisplayName ?? i.SupplierUser.LegalName).ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.PiId)
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplierInvoiceNo = i.SupplierInvoiceNo,
                    supplierId = i.SupplierUserId,
                    supplierName = (i.SupplierUser.DisplayName ?? i.SupplierUser.LegalName),
                    poId = i.PoId,
                    poNo = i.Po != null ? i.Po.PoNo : null,
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    subtotal = i.Subtotal,
                    discount = i.DiscountAmount,
                    tax = i.TaxAmount,
                    whtAmount = i.WhtAmount,
                    total = i.TotalAmount,
                    status = i.Status.StatusKey,
                    statusName = i.Status.StatusName,
                    paymentMethod = i.Method.MethodKey,
                    createdBy = i.CreatedByUser.User.FullName,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            return Ok(items.Select(i => new
            {
                i.id, i.invoiceNo, i.supplierInvoiceNo, i.supplierId, i.supplierName,
                supplierInitials = Initials(i.supplierName),
                i.poId, i.poNo, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.whtAmount, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid,
                isOverdue = i.total - i.paid > 0 && i.dueDate < Today()
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-invoice list");
        }
    }

    [HttpGet("invoices/{id:int}")]
    public async Task<IActionResult> GetPurchaseInvoice(int id)
    {
        try
        {
            var i = await _db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.PiId == id)
                .Select(x => new
                {
                    id = x.PiId,
                    invoiceNo = x.InvoiceNo,
                    supplierInvoiceNo = x.SupplierInvoiceNo,
                    supplierId = x.SupplierUserId,
                    supplierName = (x.SupplierUser.DisplayName ?? x.SupplierUser.LegalName),
                    supplierCode = x.SupplierUser.PartyCode,
                    poId = x.PoId,
                    poNo = x.Po != null ? x.Po.PoNo : null,
                    invoiceDate = x.InvoiceDate,
                    dueDate = x.DueDate,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    whtAmount = x.WhtAmount,
                    total = x.TotalAmount,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    paymentMethod = x.Method.MethodKey,
                    createdBy = x.CreatedByUser.User.FullName,
                    paid = x.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m,
                    lines = x.PurchaseInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.PiItemId,
                        lineNo = l.LineNo,
                        productId = l.ProductId,
                        sku = l.Product.Sku,
                        imageUrl = l.Product.ImageUrl,
                        name = l.Product.ProductName,
                        qty = l.Quantity,
                        unitCost = l.UnitCost,
                        taxPercent = l.TaxPercent,
                        lineTotal = l.LineTotal
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (i is null) return NotFound(new { message = $"No purchase invoice with id {id}." });

            return Ok(new
            {
                i.id, i.invoiceNo, i.supplierInvoiceNo, i.supplierId, i.supplierName,
                supplierInitials = Initials(i.supplierName),
                i.supplierCode, i.poId, i.poNo, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.whtAmount, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid, i.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase invoice {id}");
        }
    }

    // ══════════════════════════ request bodies ══════════════════════════

    /// <summary>
    /// What the admin decided in the price popup for one line.
    /// Keep = true: the product's selling price is left exactly as it is.
    /// Otherwise: average this purchase with the ticked earlier lots
    /// (BatchIds), weighted by what is left of each, and set the result --
    /// or FinalSalePrice, when the admin typed his own figure.
    /// </summary>
    public record PriceDecision(bool Keep, List<int>? BatchIds, decimal? FinalSalePrice);

    public record PoLineRequest(
        int ProductId, int Qty, decimal UnitCost,
        decimal DutyPrice, int? DutyAccountId, decimal FsPrice, decimal Margin1Price, decimal Margin2Price,
        string? DutyNote, string? FsNote, string? Margin1Note, string? Margin2Note,
        PriceDecision? Pricing);

    public record PoRequest(
        int SupplierId, int LocationId, DateOnly? PoDate, string? SupplierBillNo,
        decimal Discount, string? Notes, List<PoLineRequest> Lines);

    public record LogisticsRequest(string? Name, bool? IsActive);

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Every purchase order on the current filter, as a spreadsheet.
    /// Runs the SAME list action the screen runs, so the two cannot drift.</summary>
    [HttpGet("orders/export")]
    public async Task<IActionResult> ExportPurchaseOrders(
        [FromQuery] string? q, [FromQuery] int? supplierId, [FromQuery] int? locationId)
    {
        try
        {
            var action = await GetPurchaseOrders(q, supplierId, locationId);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("PO No", "poNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Supplier", "supplierName", XlsxWriter.CellKind.Text, 32),
                new XlsxWriter.Column("Received At", "location", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("PO Date", "poDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Lines", "itemCount", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Units", "units", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Supplier Total", "total", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Selling Value", "saleValue", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Bill", "invoiceNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Raised By", "createdBy", XlsxWriter.CellKind.Text, 20),
            };

            var bytes = XlsxWriter.FromPayload("Purchase Orders",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"purchase-orders-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the purchase orders");
        }
    }

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };
}
