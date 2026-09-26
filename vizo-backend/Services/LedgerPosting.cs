using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// The double entries a sale, a sales return and a confirmed collection make.
///
/// ─────────────────────────────── WHY THIS EXISTS ───────────────────────────────
///
/// Gap D7 in HANDOFF: a sale invoice never reached the ledger. 27 of 39
/// invoices carried no EntryId; the twelve that did were seeded. Sales returns
/// posted nothing either. So Accounts Receivable, the aged-receivables report,
/// the credit-limit check on every new order and the trial balance all
/// under-stated by everything billed through the app -- only the old
/// document-built statement was right, because it never asked the ledger.
///
/// Everything the customer ledger shows is now in the books:
///
///   SALE INVOICE      Dr 1130 Accounts Receivable (party)   total
///                     Cr 4001 Sale                          total - tax
///                     Cr 2110 Output Sales Tax Payable      tax (only if > 0)
///     paid at the counter, in the same entry:
///                     Dr cash/bank of the method            total
///                     Cr 1130 Accounts Receivable (party)   total
///
///   SALES RETURN      Dr 4002 Sales Returns                 amount - tax share
///                     Dr 2110 Output Sales Tax Payable      tax share (only if the bill carried tax)
///                     Cr 1130 Accounts Receivable (party)   amount
///
///   COLLECTION        a posted receipt voucher (RV series), through the same
///   (confirmed)       shape AccountingController.PostVoucherToLedger writes:
///                     Dr cash/bank, Cr 1130 (party). Linked back through
///                     "Collection"."VoucherId".
///
/// "total - tax" and not "Subtotal - Discount": the two are the same number on
/// every invoice this system has ever written, except when a line total was
/// rounded to the paisa -- and then only "total - tax" is guaranteed to make the
/// entry balance. It is also exactly how the twelve seeded sale entries were
/// posted (INV-26-0142: 145,000.00 = 122,768.20 + 22,231.80).
///
/// NO COST OF GOODS SOLD, on purpose. The seeded entries carry a COGS pair;
/// these do not. Session A (feat/a-purchases, same day) is introducing
/// per-purchase-order stock lots, and COGS belongs to that change: posting it
/// here at Product.CostPrice would be a second, different answer to "what did
/// this sale cost", and the one that has to be undone the day the lots arrive.
///
/// ─────────────────────────────── THE RULES ──────────────────────────────────
///
/// · IDEMPOTENT. A document with an EntryId is never posted again. That is what
///   lets the one-off backfill (POST /api/ledgers/customers/post-missing) be run
///   any number of times.
/// · Collections and vouchers are NOT double posted: a voucher already posts
///   itself in AccountingController; a collection posts only through the
///   voucher it creates, and only when it has none.
/// · Every entry is POSTED immediately, in the fiscal period of the document's
///   own date. A closed period refuses, with the reason -- the caller decides
///   whether that stops the sale (it does, inside the invoice's transaction).
/// · Static, no DI: this is a helper like SkuGenerator, not a service layer.
///   The caller passes the database and the signed-in user.
/// </summary>
public static class LedgerPosting
{
    public const string ReceivableCode = "1130";
    public const string SaleCode = "4001";
    public const string SalesReturnsCode = "4002";
    public const string OutputTaxCode = "2110";
    public const string StaffPayablesCode = "2140";
    public const string SalaryExpenseCode = "5101";
    public const string CashOnHandCode = "1101";

    private const string Posted = "POSTED";

    /// <summary>
    /// The cash or bank account money taken by a payment method lands in.
    ///
    /// In code rather than a column: there are ten methods and six cash/bank
    /// accounts, the mapping is a fact about this business, and a column would
    /// be one more thing to keep right on three branches at once. Null means
    /// the method does not move cash (CREDIT, CREDIT_NOTE) -- a counter sale on
    /// credit stays on the customer's account.
    ///
    /// "FAISAL" is the spelling migration 21 gave the method; the bank is
    /// Faysal, and its account is 1113 (migration 30).
    /// </summary>
    public static string? CashAccountCodeFor(string? methodKey) => (methodKey ?? "").ToUpperInvariant() switch
    {
        "CASH" or "PETTY_CASH" => CashOnHandCode,
        "BANK" or "CHEQUE" => "1110",
        "MEEZAN" => "1111",
        "FAISAL" or "FAYSAL" => "1113",
        "EASYPAISA" => "1120",
        "JAZZCASH" => "1121",
        _ => null
    };

    public static async Task<int?> AccountIdAsync(AppDbContext db, string code) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.AccountCode == code && !a.IsGroup)
            .Select(a => (int?)a.AccountId)
            .FirstOrDefaultAsync();

    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Same as ApiControllerBase.NextNumber -- which is protected, and this is
    /// not a controller. Same series table, same prefix rules, same known limit
    /// (not atomic; HANDOFF D6).
    /// </summary>
    public static async Task<string> NextNumberAsync(AppDbContext db, string prefix)
    {
        var series = await db.DocumentSeries.FirstOrDefaultAsync(s => s.Prefix == prefix);
        if (series is null) return $"{prefix}-{BusinessClock.Now():yyyyMMddHHmmss}";

        var n = series.NextNumber;
        series.NextNumber = n + 1;
        await db.SaveChangesAsync();

        var year = series.IncludeYear ? $"{BusinessClock.Now():yy}-" : "";
        return $"{series.Prefix}-{year}{n.ToString().PadLeft(series.Padding, '0')}";
    }

    /// <summary>One line of a new entry, before it is written.</summary>
    public sealed record Leg(int AccountId, decimal Debit, decimal Credit, string? Description,
        int? PartyUserId = null, int? StaffId = null);

    /// <summary>
    /// Writes a POSTED journal entry with the given legs, after every check a
    /// posting needs. Returns the entry, or the reason it was refused.
    /// Zero legs are dropped (the schema refuses a line of 0 on both sides).
    /// </summary>
    public static async Task<(JournalEntry? Entry, string? Error)> WriteEntryAsync(
        AppDbContext db, DateOnly date, string typeKey, int locationId,
        string? reference, string narration, int userId, IReadOnlyList<Leg> legs)
    {
        var kept = legs.Where(l => l.Debit > 0 || l.Credit > 0).ToList();
        if (kept.Count < 2) return (null, "An entry needs at least two lines with an amount.");
        if (kept.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0)))
            return (null, "A line is either a debit or a credit, and never negative.");

        var dr = kept.Sum(l => l.Debit);
        var cr = kept.Sum(l => l.Credit);
        if (dr != cr) return (null, $"The entry does not balance: debits {dr:N2} against credits {cr:N2}.");

        var period = await db.FiscalPeriods
            .FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
        if (period is null) return (null, $"No fiscal period covers {date:yyyy-MM-dd}.");
        if (period.IsClosed) return (null, $"{period.PeriodName} is closed. Reopen it, or use a date in an open month.");

        var posted = await db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
        if (posted is null) return (null, "No POSTED status is configured.");

        var type = await db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == typeKey)
                   ?? await db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "JOURNAL")
                   ?? await db.JournalEntryTypes.FirstAsync();

        var entry = new JournalEntry
        {
            EntryNo = await NextNumberAsync(db, "JV"),
            EntryDate = date,
            EntryTypeId = type.EntryTypeId,
            PeriodId = period.PeriodId,
            LocationId = locationId,
            ReferenceNo = reference,
            Narration = narration.Length > 300 ? narration[..300] : narration,
            StatusId = posted.StatusId,
            CreatedByUserId = userId,
            PostedByUserId = userId,
            CreatedAt = BusinessClock.Today()
        };
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync();

        short n = 1;
        foreach (var l in kept)
        {
            db.JournalEntryLines.Add(new JournalEntryLine
            {
                EntryId = entry.EntryId,
                LineNo = n++,
                AccountId = l.AccountId,
                PartyUserId = l.PartyUserId,
                StaffId = l.StaffId,
                Description = l.Description is { Length: > 300 } d ? d[..300] : l.Description,
                DebitAmount = Money(l.Debit),
                CreditAmount = Money(l.Credit)
            });
        }
        await db.SaveChangesAsync();

        return (entry, null);
    }

    // ══════════════════════════════════════════════════════════════════
    //  SALE INVOICE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The legs of a sale invoice. <paramref name="paidWithMethodKey"/> is the
    /// method a counter sale was paid by, or null when the bill goes on the
    /// customer's account.
    /// </summary>
    private static async Task<(List<Leg>? Legs, string? Error)> InvoiceLegsAsync(
        AppDbContext db, SalesInvoice inv, string? paidWithMethodKey)
    {
        var ar = await AccountIdAsync(db, ReceivableCode);
        var sale = await AccountIdAsync(db, SaleCode);
        var tax = await AccountIdAsync(db, OutputTaxCode);
        if (ar is null || sale is null || tax is null)
            return (null, "Accounts 1130, 4001 and 2110 must all be in the chart before a sale can post.");

        var total = Money(inv.TotalAmount);
        var taxAmt = Money(Math.Max(0m, inv.TaxAmount));
        if (taxAmt > total) taxAmt = 0m;          // nonsense on the row; post it all as sale rather than fail
        var net = total - taxAmt;

        var legs = new List<Leg>
        {
            new(ar.Value, total, 0m, $"Sale {inv.InvoiceNo}", PartyUserId: inv.CustomerUserId),
            new(sale.Value, 0m, net, $"Sale {inv.InvoiceNo}"),
            new(tax.Value, 0m, taxAmt, $"Output tax on {inv.InvoiceNo}")
        };

        var cashCode = CashAccountCodeFor(paidWithMethodKey);
        if (cashCode is not null)
        {
            var cash = await AccountIdAsync(db, cashCode);
            if (cash is null) return (null, $"Account {cashCode} is not in the chart, so the counter payment cannot post.");
            legs.Add(new Leg(cash.Value, total, 0m, $"Paid at the counter, {inv.InvoiceNo}"));
            legs.Add(new Leg(ar.Value, 0m, total, $"Paid at the counter, {inv.InvoiceNo}", PartyUserId: inv.CustomerUserId));
        }

        return (legs, null);
    }

    /// <summary>
    /// Posts a sale invoice, once. Returns null on success or when there is
    /// nothing to do (already posted, or a bill of zero); the refusal otherwise.
    /// Call it INSIDE the transaction that wrote the invoice, so a refusal
    /// (a closed month) stops the bill rather than leaving it off the books.
    /// </summary>
    public static async Task<string?> PostSalesInvoiceAsync(
        AppDbContext db, int invoiceId, int userId, string? paidWithMethodKey = null)
    {
        var inv = await db.SalesInvoices.FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (inv is null) return $"No invoice {invoiceId}.";
        if (inv.EntryId is not null) return null;
        if (inv.TotalAmount <= 0) return null;

        var (legs, why) = await InvoiceLegsAsync(db, inv, paidWithMethodKey);
        if (legs is null) return why;

        var party = await db.Parties.AsNoTracking()
            .Where(p => p.UserId == inv.CustomerUserId)
            .Select(p => p.DisplayName ?? p.LegalName).FirstOrDefaultAsync();

        var (entry, error) = await WriteEntryAsync(db, inv.InvoiceDate, "SALE", inv.LocationId,
            inv.InvoiceNo, $"Sale invoice {inv.InvoiceNo}" + (party is null ? "" : $" -- {party}"),
            userId, legs);
        if (entry is null) return $"{inv.InvoiceNo} could not post: {error}";

        inv.EntryId = entry.EntryId;
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>
    /// After an invoice was edited (UpdateOrder rewrites it to follow its
    /// order), rewrites the SAME entry's lines to match, keeping its number.
    ///
    /// Rewritten in place rather than reversed and re-posted: the entry is the
    /// system's own mirror of one document, not something a person wrote, and
    /// Invoiced/Edit happens BEFORE the goods leave -- a reversal pair for every
    /// price the office corrects would fill the customer's statement with
    /// entries that say nothing. A CLOSED month is the exception: the entry is
    /// left alone and the reason returned, because a closed month's figures
    /// must not move under the accountant.
    /// </summary>
    public static async Task<string?> RepostSalesInvoiceAsync(AppDbContext db, int invoiceId, int userId)
    {
        var inv = await db.SalesInvoices.FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (inv is null) return $"No invoice {invoiceId}.";
        if (inv.EntryId is null) return await PostSalesInvoiceAsync(db, invoiceId, userId);

        var entry = await db.JournalEntries.Include(e => e.JournalEntryLines).Include(e => e.Period)
            .FirstOrDefaultAsync(e => e.EntryId == inv.EntryId);
        if (entry is null) { inv.EntryId = null; return await PostSalesInvoiceAsync(db, invoiceId, userId); }
        if (entry.Period.IsClosed)
            return $"{inv.InvoiceNo} was changed, but its entry {entry.EntryNo} sits in {entry.Period.PeriodName}, "
                 + "which is closed -- the books were not moved. Post a correction in an open month.";

        /* Was it paid at the counter? The entry says so: a debit on a cash or
           bank account. Keep that half, at the new total. */
        var cashCodes = new[] { "1101", "1102", "1110", "1111", "1112", "1113", "1120", "1121" };
        var cashAccounts = await db.Accounts.AsNoTracking()
            .Where(a => cashCodes.Contains(a.AccountCode))
            .ToDictionaryAsync(a => a.AccountId, a => a.AccountCode);
        var cashLine = entry.JournalEntryLines.FirstOrDefault(l => l.DebitAmount > 0 && cashAccounts.ContainsKey(l.AccountId));
        string? methodKey = null;
        if (cashLine is not null)
        {
            var code = cashAccounts[cashLine.AccountId];
            methodKey = code switch
            {
                "1110" => "BANK", "1111" => "MEEZAN", "1113" => "FAISAL",
                "1120" => "EASYPAISA", "1121" => "JAZZCASH", _ => "CASH"
            };
        }

        var (legs, why) = await InvoiceLegsAsync(db, inv, methodKey);
        if (legs is null) return why;
        var kept = legs.Where(l => l.Debit > 0 || l.Credit > 0).ToList();

        db.JournalEntryLines.RemoveRange(entry.JournalEntryLines);
        await db.SaveChangesAsync();

        short n = 1;
        foreach (var l in kept)
        {
            db.JournalEntryLines.Add(new JournalEntryLine
            {
                EntryId = entry.EntryId, LineNo = n++, AccountId = l.AccountId,
                PartyUserId = l.PartyUserId, Description = l.Description,
                DebitAmount = Money(l.Debit), CreditAmount = Money(l.Credit)
            });
        }
        entry.EntryDate = inv.InvoiceDate;
        await db.SaveChangesAsync();
        return null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  SALES RETURN
    // ══════════════════════════════════════════════════════════════════

    /// <summary>What a return credits: its lines at the price the customer paid.</summary>
    public static async Task<decimal> ReturnAmountAsync(AppDbContext db, int returnId) =>
        Money(await db.SalesReturnItems.Where(l => l.ReturnId == returnId)
            .SumAsync(l => (decimal?)(l.Quantity * l.UnitPrice)) ?? 0m);

    /// <summary>
    /// Posts a sales return, once. Rejected and draft returns post nothing.
    ///
    /// The return's unit price is what the customer paid per piece -- LineTotal
    /// over quantity, tax INCLUDED (SalesController.CreateReturn). When the
    /// return points at one bill that carried tax, the tax share of the credit
    /// goes back against Output Tax rather than all of it against 4002, so
    /// Output Tax is not left overstated. Tax is 0% now, so on anything new
    /// this is simply Dr 4002 / Cr 1130.
    ///
    /// A cash REFUND is not posted here: the return puts the credit on the
    /// customer's account, which is what every return this system has taken
    /// actually did. Money handed back is a payment voucher to the customer,
    /// written by Accounts -- recorded in NOTES-b.md.
    /// </summary>
    public static async Task<string?> PostSalesReturnAsync(AppDbContext db, int returnId, int userId)
    {
        var ret = await db.SalesReturns.Include(r => r.Status).FirstOrDefaultAsync(r => r.ReturnId == returnId);
        if (ret is null) return $"No return {returnId}.";
        if (ret.EntryId is not null) return null;
        if (ret.Status.StatusKey is "REJECTED" or "DRAFT") return null;

        var amount = await ReturnAmountAsync(db, returnId);
        if (amount <= 0) return null;

        var ar = await AccountIdAsync(db, ReceivableCode);
        var back = await AccountIdAsync(db, SalesReturnsCode);
        var taxAcc = await AccountIdAsync(db, OutputTaxCode);
        if (ar is null || back is null || taxAcc is null)
            return "Accounts 1130, 4002 and 2110 must all be in the chart before a return can post.";

        var taxShare = 0m;
        if (ret.InvoiceId is not null)
        {
            var bill = await db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceId == ret.InvoiceId)
                .Select(i => new { i.TaxAmount, i.TotalAmount }).FirstOrDefaultAsync();
            if (bill is not null && bill.TaxAmount > 0 && bill.TotalAmount > 0)
                taxShare = Money(amount * bill.TaxAmount / bill.TotalAmount);
        }

        var party = await db.Parties.AsNoTracking()
            .Where(p => p.UserId == ret.CustomerUserId)
            .Select(p => p.DisplayName ?? p.LegalName).FirstOrDefaultAsync();

        var (entry, error) = await WriteEntryAsync(db, ret.ReturnDate, "SALES_RETURN", ret.LocationId,
            ret.ReturnNo, $"Sales return {ret.ReturnNo}" + (party is null ? "" : $" -- {party}"), userId,
            new[]
            {
                new Leg(back.Value, amount - taxShare, 0m, $"Return {ret.ReturnNo}"),
                new Leg(taxAcc.Value, taxShare, 0m, $"Output tax reversed, {ret.ReturnNo}"),
                new Leg(ar.Value, 0m, amount, $"Return {ret.ReturnNo}", PartyUserId: ret.CustomerUserId)
            });
        if (entry is null) return $"{ret.ReturnNo} could not post: {error}";

        ret.EntryId = entry.EntryId;
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>
    /// A posted return that is then rejected: its entry is reversed by a mirror
    /// dated today, the way AccountingController reverses any entry -- the
    /// original keeps POSTED and gains ReversedByEntryId, so both count and
    /// cancel. The return keeps pointing at the original, so "was this ever
    /// posted" still has an answer.
    /// </summary>
    public static async Task<string?> ReverseSalesReturnAsync(AppDbContext db, int returnId, int userId, string? reason)
    {
        var ret = await db.SalesReturns.FirstOrDefaultAsync(r => r.ReturnId == returnId);
        if (ret?.EntryId is null) return null;

        var original = await db.JournalEntries.Include(e => e.JournalEntryLines)
            .FirstOrDefaultAsync(e => e.EntryId == ret.EntryId);
        if (original is null || original.ReversedByEntryId is not null) return null;

        var (mirror, error) = await WriteEntryAsync(db, BusinessClock.Today(), "SALES_RETURN", original.LocationId,
            original.EntryNo, $"Reversal of {original.EntryNo} -- {ret.ReturnNo} rejected"
                              + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason.Trim()}"),
            userId,
            original.JournalEntryLines.OrderBy(l => l.LineNo)
                .Select(l => new Leg(l.AccountId, l.CreditAmount, l.DebitAmount,
                    $"Reversal: {l.Description}", l.PartyUserId, l.StaffId))
                .ToList());
        if (mirror is null) return $"{ret.ReturnNo} was rejected but its entry could not be reversed: {error}";

        original.ReversedByEntryId = mirror.EntryId;
        await db.SaveChangesAsync();
        return null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  COLLECTION
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A confirmed collection that has no voucher gets one: a POSTED receipt
    /// voucher in the RV series, into the cash or bank account of its method,
    /// posted the same way every other receipt voucher is. The customer's
    /// statement then reads "RV-26-0514 · Faysal Bank Account" like any receipt.
    ///
    /// A collection that already has a voucher (the five seeded ones) is left
    /// alone -- that voucher posted itself.
    /// </summary>
    public static async Task<string?> PostCollectionAsync(AppDbContext db, int collectionId, int userId)
    {
        var c = await db.Collections.Include(x => x.Method).Include(x => x.Status)
            .FirstOrDefaultAsync(x => x.CollectionId == collectionId);
        if (c is null) return $"No collection {collectionId}.";
        if (c.VoucherId is not null || c.Status.StatusKey != "CONFIRMED" || c.Amount <= 0) return null;

        var cashCode = CashAccountCodeFor(c.Method.MethodKey) ?? CashOnHandCode;
        var cash = await AccountIdAsync(db, cashCode);
        var ar = await AccountIdAsync(db, ReceivableCode);
        if (cash is null || ar is null) return $"Accounts {cashCode} and 1130 must be in the chart.";

        var receipt = await db.VoucherTypes.FirstOrDefaultAsync(t => t.TypeCode == (cashCode == CashOnHandCode ? "CR" : "BR"))
                      ?? await db.VoucherTypes.FirstOrDefaultAsync(t => t.IsReceipt);
        var posted = await db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
        if (receipt is null || posted is null) return "Receipt voucher types or posting statuses are not configured.";

        var locationId = await db.Locations.AsNoTracking()
            .Where(l => l.IsActive).OrderByDescending(l => l.IsDefault).ThenBy(l => l.LocationId)
            .Select(l => l.LocationId).FirstAsync();

        var v = new Voucher
        {
            VoucherNo = await NextNumberAsync(db, "RV"),
            VoucherTypeId = receipt.VoucherTypeId,
            VoucherDate = c.CollectedOn,
            LocationId = locationId,
            PartyUserId = c.CustomerUserId,
            CashBankAccountId = cash.Value,
            Amount = c.Amount,
            MethodId = c.MethodId,
            ReferenceNo = c.ReferenceNo ?? c.ReceiptNo,
            Narration = $"Collection {c.ReceiptNo}",
            StatusId = posted.StatusId,
            CreatedByUserId = userId
        };
        db.Vouchers.Add(v);
        await db.SaveChangesAsync();

        var (entry, error) = await WriteEntryAsync(db, c.CollectedOn, "RECEIPT", locationId, v.VoucherNo,
            $"Collection {c.ReceiptNo}", userId, new[]
            {
                new Leg(cash.Value, c.Amount, 0m, $"Received against {v.VoucherNo}"),
                new Leg(ar.Value, 0m, c.Amount, $"Settled by {v.VoucherNo}", PartyUserId: c.CustomerUserId)
            });
        if (entry is null) return $"{c.ReceiptNo} could not post: {error}";

        v.EntryId = entry.EntryId;
        c.VoucherId = v.VoucherId;
        await db.SaveChangesAsync();
        return null;
    }
}
