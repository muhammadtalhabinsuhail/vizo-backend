using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// THE VOUCHER BOOK -- many entries for one date, typed into a table (the
/// owner, 22 Oct, with photographs of the client's old "Receipt Vouchers Book"
/// screen).
///
/// The old system took a day's receipts as rows in one grid: entry date at the
/// top, then per row the account credited (the customer), the particulars, the
/// amount and the account debited (cash in hand or a bank), every row POSTED.
/// The one-voucher-at-a-time form stays exactly as it is; this is a second way
/// in for the person entering a whole day at once.
///
/// EVERY ROW CARRIES A CODE, and the code is what it is:
///
///   CR  Cash Receipt   BR  Bank Receipt     -- money in from a party
///   CP  Cash Payment   BP  Bank Payment     -- money out to a party
///   JV  Journal Voucher                     -- one account debited, another
///                                              credited, no cash moving
///
/// Receipts and payments become ordinary "Voucher" rows through the same
/// ValidateVoucher and PostVoucherToLedger the single form uses, so a voucher
/// typed here and one typed there are indistinguishable. A JV becomes a
/// two-line journal entry (type JOURNAL), because a voucher row can only name a
/// party and a cash/bank account, and a JV names two accounts of any kind.
///
/// WHO: the Super Admin may use every code. Everybody else -- the accountant --
/// gets CR and BR only, sees only receipts in the day's list, and is refused a
/// payment or a JV here exactly as on the single form (SeesPayments).
///
/// The whole table is saved in ONE transaction: a mistake on row 9 saves none
/// of the 14, and says which row. Half a day's book is harder to put right
/// than none of it.
/// </summary>
public partial class AccountingController
{
    /// <summary>The codes this person may enter, in the order the dropdown shows them.</summary>
    private async Task<List<VoucherBookCode>> BookCodesAsync()
    {
        var types = await _db.VoucherTypes.AsNoTracking()
            .Where(t => t.TypeCode == "CR" || t.TypeCode == "BR" || t.TypeCode == "CP" || t.TypeCode == "BP")
            .Select(t => new { t.TypeCode, t.TypeName, t.IsReceipt })
            .ToListAsync();

        var order = new[] { "CR", "BR", "CP", "BP" };
        var codes = types
            .Where(t => t.IsReceipt || SeesPayments())
            .OrderBy(t => Array.IndexOf(order, t.TypeCode))
            .Select(t => new VoucherBookCode(t.TypeCode, t.TypeName, t.IsReceipt ? "receipt" : "payment"))
            .ToList();

        if (SeesPayments()) codes.Add(new VoucherBookCode("JV", "Journal Voucher", "journal"));
        return codes;
    }

    /// <summary>
    /// One date's entries, newest last (the order they were typed in), and the
    /// codes this person may use. <paramref name="q"/> narrows by number,
    /// account or particulars.
    /// </summary>
    [HttpGet("voucher-book")]
    public async Task<IActionResult> GetVoucherBook([FromQuery] DateOnly? date, [FromQuery] string? q)
    {
        try
        {
            var day = date ?? Today();

            var vouchers = _db.Vouchers.AsNoTracking()
                .Where(v => v.VoucherDate == day && v.VoucherType.TypeCode != "WR" && v.VoucherType.TypeCode != "WP");
            if (!SeesPayments()) vouchers = vouchers.Where(v => v.VoucherType.IsReceipt);

            var rows = (await vouchers
                .OrderBy(v => v.VoucherId)
                .Select(v => new
                {
                    v.VoucherId, v.VoucherNo,
                    code = v.VoucherType.TypeCode, v.VoucherType.IsReceipt,
                    party = v.PartyUser != null ? (v.PartyUser.DisplayName ?? v.PartyUser.LegalName) : null,
                    partyCode = v.PartyUser != null ? v.PartyUser.PartyCode : null,
                    cash = v.CashBankAccount != null ? v.CashBankAccount.AccountCode + " " + v.CashBankAccount.AccountName : null,
                    v.Narration, v.Amount,
                    status = v.Status.StatusKey, statusName = v.Status.StatusName,
                    method = v.Method.MethodName,
                    createdBy = v.CreatedByUser.FullName
                })
                .ToListAsync())
                .Select(v =>
                {
                    var partyLabel = v.party is null ? "—" : $"{v.partyCode} {v.party}";
                    return new VoucherBookLine(
                        "voucher", v.VoucherId, v.VoucherNo, v.code,
                        /* Receipt: the party is credited, the cash debited. A
                           payment is the other way round. */
                        AccountCr: v.IsReceipt ? partyLabel : v.cash ?? "—",
                        AccountDr: v.IsReceipt ? v.cash ?? "—" : partyLabel,
                        v.Narration, v.Amount, v.status, v.statusName, v.method, v.createdBy,
                        $"/accounting/vouchers/{v.VoucherId}");
                })
                .ToList();

            /* Journal vouchers -- hand-written entries of type JOURNAL on this
               date. The Super Admin's alone. */
            if (SeesPayments())
            {
                var journals = await _db.JournalEntries.AsNoTracking()
                    .Where(e => e.EntryDate == day && e.EntryType.TypeKey == "JOURNAL")
                    .OrderBy(e => e.EntryId)
                    .Select(e => new
                    {
                        e.EntryId, e.EntryNo, e.Narration,
                        status = e.Status.StatusKey, statusName = e.Status.StatusName,
                        createdBy = e.CreatedByUser.FullName,
                        lines = e.JournalEntryLines.OrderBy(l => l.LineNo).Select(l => new
                        {
                            account = l.Account.AccountCode + " " + l.Account.AccountName,
                            party = l.PartyUser != null ? (l.PartyUser.DisplayName ?? l.PartyUser.LegalName) : null,
                            l.DebitAmount, l.CreditAmount
                        }).ToList()
                    })
                    .ToListAsync();

                string Side(IEnumerable<dynamic> side)
                {
                    var list = side.ToList();
                    if (list.Count == 0) return "—";
                    if (list.Count > 1) return $"{list.Count} accounts";
                    var one = list[0];
                    return one.party is null ? (string)one.account : $"{one.account} — {one.party}";
                }

                rows.AddRange(journals.Select(j => new VoucherBookLine(
                    "journal", j.EntryId, j.EntryNo, "JV",
                    AccountCr: Side(j.lines.Where(l => l.CreditAmount > 0)),
                    AccountDr: Side(j.lines.Where(l => l.DebitAmount > 0)),
                    j.Narration, j.lines.Sum(l => l.DebitAmount),
                    j.status, j.statusName, null, j.createdBy,
                    $"/accounting/journal-entries/{j.EntryId}")));
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                rows = rows.Where(r =>
                    r.No.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    r.AccountCr.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    r.AccountDr.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.Particulars ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return Ok(new
            {
                date = day,
                codes = await BookCodesAsync(),
                canSeePayments = SeesPayments(),
                rows,
                receiptTotal = rows.Where(r => r.Code is "CR" or "BR" && r.Status == Posted).Sum(r => r.Amount),
                paymentTotal = rows.Where(r => r.Code is "CP" or "BP" && r.Status == Posted).Sum(r => r.Amount),
                journalTotal = rows.Where(r => r.Code == "JV" && r.Status == Posted).Sum(r => r.Amount)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the voucher book");
        }
    }

    /// <summary>
    /// Saves every row of the table for one date, all or nothing.
    /// </summary>
    [HttpPost("voucher-book")]
    public async Task<IActionResult> SaveVoucherBook([FromBody] VoucherBookRequest body)
    {
        try
        {
            if (body.Rows is null || body.Rows.Count == 0)
                return BadRequest(new { message = "Add at least one row." });
            if (body.Rows.Count > 100)
                return BadRequest(new { message = "Save at most 100 rows at a time." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });

            var allowed = (await BookCodesAsync()).Select(c => c.Code).ToHashSet();
            var types = await _db.VoucherTypes.AsNoTracking().ToDictionaryAsync(t => t.TypeCode);
            var methods = await _db.PaymentMethods.AsNoTracking().Where(m => m.IsActive).ToListAsync();

            var draft = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
            if (draft is null || posted is null)
                return BadRequest(new { message = "Posting statuses are not configured." });

            var journalType = await _db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "JOURNAL")
                              ?? await _db.JournalEntryTypes.FirstAsync();

            await using var tx = await _db.Database.BeginTransactionAsync();

            var savedVouchers = new List<Voucher>();
            var savedJournals = new List<JournalEntry>();

            for (var i = 0; i < body.Rows.Count; i++)
            {
                var r = body.Rows[i];
                var at = $"Row {i + 1}";
                var code = (r.Code ?? "").Trim().ToUpperInvariant();

                if (!allowed.Contains(code))
                    return BadRequest(new
                    {
                        message = SeesPayments() || code is not ("CP" or "BP" or "JV")
                            ? $"{at}: pick a code." : $"{at}: {ReceiptsOnly}",
                        row = i
                    });
                if (r.Amount <= 0)
                    return BadRequest(new { message = $"{at}: the amount must be above zero.", row = i });

                if (code == "JV")
                {
                    var why = await CheckJournalSide(r.DebitAccountId, r.DebitPartyId, "debit")
                              ?? await CheckJournalSide(r.CreditAccountId, r.CreditPartyId, "credit");
                    if (why is not null) return BadRequest(new { message = $"{at}: {why}", row = i });
                    if (r.DebitAccountId == r.CreditAccountId && r.DebitPartyId == r.CreditPartyId)
                        return BadRequest(new { message = $"{at}: the debit and credit sides are the same account.", row = i });

                    var period = await _db.FiscalPeriods
                        .FirstOrDefaultAsync(p => p.StartDate <= body.Date && p.EndDate >= body.Date);
                    if (period is null)
                        return BadRequest(new { message = $"No fiscal period covers {body.Date:yyyy-MM-dd}.", row = i });
                    if (period.IsClosed)
                        return BadRequest(new { message = $"{period.PeriodName} is closed. Reopen it or use another date.", row = i });

                    var entry = new JournalEntry
                    {
                        EntryNo = await NextNumber("JV"),
                        EntryDate = body.Date,
                        EntryTypeId = journalType.EntryTypeId,
                        PeriodId = period.PeriodId,
                        LocationId = body.LocationId,
                        ReferenceNo = r.Reference,
                        Narration = r.Particulars?.Trim() ?? "",
                        StatusId = body.Post ? posted.StatusId : draft.StatusId,
                        CreatedByUserId = CurrentUserId(),
                        PostedByUserId = body.Post ? CurrentUserId() : null,
                        CreatedAt = Today()
                    };
                    _db.JournalEntries.Add(entry);
                    await _db.SaveChangesAsync();

                    _db.JournalEntryLines.AddRange(
                        new JournalEntryLine
                        {
                            EntryId = entry.EntryId, LineNo = 1, AccountId = r.DebitAccountId!.Value,
                            PartyUserId = r.DebitPartyId, Description = r.Particulars?.Trim(),
                            DebitAmount = r.Amount, CreditAmount = 0m
                        },
                        new JournalEntryLine
                        {
                            EntryId = entry.EntryId, LineNo = 2, AccountId = r.CreditAccountId!.Value,
                            PartyUserId = r.CreditPartyId, Description = r.Particulars?.Trim(),
                            DebitAmount = 0m, CreditAmount = r.Amount
                        });
                    await _db.SaveChangesAsync();
                    savedJournals.Add(entry);
                    continue;
                }

                var type = types[code];
                if (r.CashBankAccountId is null)
                    return BadRequest(new
                    {
                        message = $"{at}: say which cash or bank account the money {(type.IsReceipt ? "went into" : "came out of")}.",
                        row = i
                    });

                /* The method follows the code when the row does not say: cash
                   for CR/CP, the first bank method for BR/BP (receipts from the
                   short receiving list, as on the single form). */
                var methodId = r.MethodId ?? (code is "CR" or "CP"
                    ? methods.FirstOrDefault(m => m.MethodKey == "CASH")?.MethodId
                    : methods.Where(m => m.MethodKind == "bank" && (!type.IsReceipt || m.IsForReceiving))
                             .OrderBy(m => m.MethodId).Select(m => (int?)m.MethodId).FirstOrDefault());
                if (methodId is null)
                    return BadRequest(new { message = $"{at}: pick a method.", row = i });

                var req = new VoucherRequest(type.VoucherTypeId, body.Date, body.LocationId, r.PartyId,
                    r.CashBankAccountId, r.Amount, methodId.Value, null, r.Reference, null,
                    r.Particulars?.Trim(), body.Post, null);
                var invalid = await ValidateVoucher(req);
                if (invalid is not null) return BadRequest(new { message = $"{at}: {invalid}", row = i });

                var v = new Voucher
                {
                    VoucherNo = await NextNumber(type.IsReceipt ? "RV" : "PV"),
                    VoucherTypeId = type.VoucherTypeId,
                    VoucherDate = body.Date,
                    LocationId = body.LocationId,
                    PartyUserId = r.PartyId,
                    CashBankAccountId = r.CashBankAccountId,
                    Amount = r.Amount,
                    MethodId = methodId.Value,
                    ReferenceNo = r.Reference,
                    Narration = r.Particulars?.Trim() ?? "",
                    StatusId = body.Post ? posted.StatusId : draft.StatusId,
                    CreatedByUserId = CurrentUserId()
                };
                _db.Vouchers.Add(v);
                await _db.SaveChangesAsync();

                if (body.Post)
                {
                    var why = await PostVoucherToLedger(v);
                    if (why is not null) return BadRequest(new { message = $"{at}: {why}", row = i });
                    await _db.SaveChangesAsync();
                }
                savedVouchers.Add(v);
            }

            await tx.CommitAsync();

            foreach (var v in savedVouchers)
                await Log(types.Values.First(t => t.VoucherTypeId == v.VoucherTypeId).IsReceipt ? "RECEIPT_RECORDED" : "PAYMENT_RECORDED",
                    "Voucher", v.VoucherNo, $"{v.Amount:N2} (voucher book)", 2);
            foreach (var e in savedJournals)
                await Log(body.Post ? "JOURNAL_POSTED" : "JOURNAL_DRAFTED", "JournalEntry", e.EntryNo, "voucher book", 2);

            /* One line to the owner for the whole table, not one per row. */
            var total = body.Rows.Sum(r => r.Amount);
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.VoucherPosted,
                $"{body.Rows.Count} {(body.Rows.Count == 1 ? "entry" : "entries")} {(body.Post ? "posted" : "saved")} by {CurrentUserName()}",
                $"Voucher book for {body.Date:dd MMM yyyy} -- PKR {total:N0}.",
                url: $"/accounting/vouchers/table?date={body.Date:yyyy-MM-dd}",
                exceptUserId: CurrentUserId());

            /* The PDFs, as every document gets them on creation. After the
               commit and swallowing their own failures, as on the single form. */
            foreach (var v in savedVouchers)
                await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "voucher", v.VoucherId, CurrentUserId());
            foreach (var e in savedJournals)
                await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "journal-entry", e.EntryId, CurrentUserId());

            return Ok(new
            {
                saved = savedVouchers.Count + savedJournals.Count,
                numbers = savedVouchers.Select(v => v.VoucherNo).Concat(savedJournals.Select(e => e.EntryNo)),
                message = $"{body.Rows.Count} {(body.Rows.Count == 1 ? "entry" : "entries")} " +
                          $"{(body.Post ? "saved and posted" : "saved as drafts")} for {body.Date:dd MMM yyyy}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the voucher book");
        }
    }

    /// <summary>One side of a JV: a real, postable account, and the party if one is named.</summary>
    private async Task<string?> CheckJournalSide(int? accountId, int? partyId, string side)
    {
        if (accountId is null) return $"pick the account to {side}.";
        if (!await _db.Accounts.AnyAsync(a => a.AccountId == accountId && !a.IsGroup && a.IsActive))
            return $"the {side} account is missing, closed, or a group heading that cannot take a posting.";
        if (partyId is not null && !await _db.Parties.AnyAsync(p => p.UserId == partyId))
            return $"the {side} party does not exist.";
        return null;
    }

    public record VoucherBookCode(string Code, string Name, string Kind);

    public record VoucherBookLine(
        string Kind, int Id, string No, string Code, string AccountCr, string AccountDr,
        string? Particulars, decimal Amount, string Status, string StatusName,
        string? Method, string? CreatedBy, string Url);

    public record VoucherBookRow(
        string Code, int? PartyId, int? CashBankAccountId, int? MethodId,
        int? DebitAccountId, int? DebitPartyId, int? CreditAccountId, int? CreditPartyId,
        decimal Amount, string? Particulars, string? Reference);

    public record VoucherBookRequest(DateOnly Date, int LocationId, bool Post, List<VoucherBookRow> Rows);
}
