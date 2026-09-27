using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// STARTING a bank reconciliation and LOADING its statement -- the half the
/// Bank Reconciliation screen never had.
///
/// AccountingController has always been able to list reconciliations, open
/// one, match a statement line to a ledger line and finalise. But nothing could
/// create a reconciliation or put a single statement line into one: the five on
/// live were seeded, and the screen apologised for it ("statement lines come
/// from the database"). This controller is that missing half:
///
///   GET    reconciliation/bank-accounts          the Cash &amp; Bank accounts, each
///                                                with where its last statement ended
///   POST   reconciliation                        start one: account, period, balances
///   PUT    reconciliation/{id}                   correct the header while not finalised
///   DELETE reconciliation/{id}                   throw away one not finalised
///   POST   reconciliation/{id}/lines             one statement line by hand
///   DELETE reconciliation/{id}/lines/{lineId}    remove an unmatched line
///   GET    reconciliation/import/template        the .xlsx to fill in
///   POST   reconciliation/{id}/import/preview    read an .xlsx / .csv, check every row, write nothing
///   POST   reconciliation/{id}/import/commit     write the checked rows, all or nothing
///
/// Matching and finalising stay where they are; this only feeds them.
///
/// SIGNS. A statement line's Amount is signed the way the BANK reads it:
/// positive is money INTO the account (a deposit, a credit on the statement),
/// negative is money out. That is the convention the existing match and
/// finalise endpoints already use -- finalise checks that the lines add up to
/// closing minus opening -- so an import turns Deposit / Withdrawal (or
/// Credit / Debit) columns into one signed figure.
///
/// Who: the same as the rest of the reconciliation screen -- the Accountant
/// policy (accountant and super-admin), plus ledger.manage, which is what the
/// menu entry is gated on.
/// </summary>
[Route("api/accounting/reconciliation")]
[ApiController]
[Authorize(Policy = "Accountant")]
[Authorize(Policy = "perm:ledger.manage")]
public class BankReconciliationController : ApiControllerBase
{
    public BankReconciliationController(AppDbContext db, IConfiguration cfg,
        ILogger<BankReconciliationController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /// <summary>The account type a statement can be reconciled against.</summary>
    private const string CashAndBankType = "Cash & Bank";

    /// <summary>Most rows one import may carry -- a year of a busy account.</summary>
    private const int MaxImportRows = 5000;

    // ══════════════════════════════════════════════════════════════════
    //  WHICH ACCOUNT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every active, postable Cash &amp; Bank account from the chart, and for each
    /// one where its last statement ENDED -- the new one normally starts the
    /// day after, at the balance the last one closed on, and the form fills
    /// both in. Read from the chart, not a list: a bank added at
    /// /accounting/coa appears here at once.
    /// </summary>
    [HttpGet("bank-accounts")]
    public async Task<IActionResult> BankAccounts()
    {
        try
        {
            var accounts = await _db.Accounts.AsNoTracking()
                .Where(a => a.IsActive && !a.IsGroup && a.AccountType.TypeName == CashAndBankType)
                .OrderBy(a => a.AccountCode)
                .Select(a => new { id = a.AccountId, code = a.AccountCode, name = a.AccountName })
                .ToListAsync();

            var ids = accounts.Select(a => a.id).ToList();
            var last = (await _db.BankReconciliations.AsNoTracking()
                    .Where(r => ids.Contains(r.AccountId))
                    .Select(r => new { r.AccountId, r.StatementDate, r.ClosingBalance, r.FinalizedOn })
                    .ToListAsync())
                .GroupBy(r => r.AccountId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StatementDate).First());

            return Ok(new
            {
                today = Today(),
                items = accounts.Select(a =>
                {
                    last.TryGetValue(a.id, out var l);
                    return new
                    {
                        a.id, a.code, a.name,
                        lastStatementDate = l?.StatementDate,
                        lastClosingBalance = l?.ClosingBalance,
                        lastFinalized = l?.FinalizedOn != null,
                        suggestedFrom = l?.StatementDate.AddDays(1)
                    };
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the bank accounts for reconciliation");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE RECONCILIATION ITSELF
    // ══════════════════════════════════════════════════════════════════

    public record ReconciliationRequest(
        int AccountId, DateOnly? PeriodFrom, DateOnly? StatementDate,
        decimal OpeningBalance, decimal ClosingBalance);

    /// <summary>
    /// Starts a reconciliation in DRAFT. Refused: an account that is not an
    /// active, postable Cash &amp; Bank account; a period that ends before it
    /// starts or ends in the future (a statement is of days that have
    /// happened); and a period that overlaps a statement this account already
    /// has -- two reconciliations claiming the same days would each try to
    /// match the same ledger lines.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReconciliationRequest body)
    {
        try
        {
            var problem = await CheckHeader(body, exceptId: null);
            if (problem is not null) return BadRequest(new { message = problem });

            var preparer = await CurrentEmployeeId();
            if (preparer is null)
                return BadRequest(new { message = "Only a staff member can prepare a reconciliation." });

            var draft = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            if (draft is null) return BadRequest(new { message = "No DRAFT status is configured." });

            var recon = new BankReconciliation
            {
                AccountId = body.AccountId,
                PeriodFrom = body.PeriodFrom,
                StatementDate = body.StatementDate!.Value,
                OpeningBalance = Math.Round(body.OpeningBalance, 2),
                ClosingBalance = Math.Round(body.ClosingBalance, 2),
                StatusId = draft.StatusId,
                PreparedByUserId = preparer.Value
            };
            _db.BankReconciliations.Add(recon);
            await _db.SaveChangesAsync();

            var accountName = await _db.Accounts.Where(a => a.AccountId == body.AccountId)
                .Select(a => a.AccountName).FirstAsync();
            await Log("RECONCILIATION_STARTED", "BankReconciliation", recon.ReconciliationId.ToString(),
                $"{accountName} {body.PeriodFrom:yyyy-MM-dd} to {body.StatementDate:yyyy-MM-dd}, " +
                $"opening {body.OpeningBalance:N2}, closing {body.ClosingBalance:N2}", 1);

            return Ok(new
            {
                id = recon.ReconciliationId,
                warning = await ContinuityWarning(recon),
                message = $"Reconciliation started for {accountName}. Load the statement next."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "start a bank reconciliation");
        }
    }

    /// <summary>
    /// Corrects the period or the balances of a reconciliation that is not
    /// finalised -- a mistyped closing balance should not mean starting again.
    /// The account cannot change once lines exist: they were checked against it.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ReconciliationRequest body)
    {
        try
        {
            var recon = await _db.BankReconciliations.Include(r => r.BankStatementLines)
                .FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });
            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is finalised and cannot be changed." });
            if (body.AccountId != recon.AccountId && recon.BankStatementLines.Count > 0)
                return BadRequest(new { message = "The account cannot change once statement lines are loaded. Remove them first, or start a new reconciliation." });

            var problem = await CheckHeader(body, exceptId: id);
            if (problem is not null) return BadRequest(new { message = problem });

            if (body.PeriodFrom is DateOnly f && body.StatementDate is DateOnly t)
            {
                var outside = recon.BankStatementLines.Count(l => l.LineDate < f || l.LineDate > t);
                if (outside > 0)
                    return BadRequest(new { message = $"{outside} statement {(outside == 1 ? "line is" : "lines are")} dated outside {f:dd MMM yyyy} – {t:dd MMM yyyy}. Remove {(outside == 1 ? "it" : "them")} first." });
            }

            recon.AccountId = body.AccountId;
            recon.PeriodFrom = body.PeriodFrom;
            recon.StatementDate = body.StatementDate!.Value;
            recon.OpeningBalance = Math.Round(body.OpeningBalance, 2);
            recon.ClosingBalance = Math.Round(body.ClosingBalance, 2);
            await _db.SaveChangesAsync();
            await Log("RECONCILIATION_EDITED", "BankReconciliation", id.ToString(),
                $"{body.PeriodFrom:yyyy-MM-dd} to {body.StatementDate:yyyy-MM-dd}, opening {body.OpeningBalance:N2}, closing {body.ClosingBalance:N2}", 1);

            return Ok(new { id, warning = await ContinuityWarning(recon), message = "Reconciliation updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"update reconciliation {id}");
        }
    }

    /// <summary>
    /// Throws away a reconciliation that is not finalised, with its statement
    /// lines. Nothing in the books changes: a statement line is the bank's
    /// word, and matching it only ever pointed at a ledger line.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var recon = await _db.BankReconciliations.Include(r => r.BankStatementLines)
                .Include(r => r.Account)
                .FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });
            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "A finalised reconciliation is the record of a signed-off statement and cannot be deleted." });

            var lines = recon.BankStatementLines.Count;
            _db.BankStatementLines.RemoveRange(recon.BankStatementLines);
            _db.BankReconciliations.Remove(recon);
            await _db.SaveChangesAsync();
            await Log("RECONCILIATION_DELETED", "BankReconciliation", id.ToString(),
                $"{recon.Account.AccountName} to {recon.StatementDate:yyyy-MM-dd}, {lines} lines", 2);

            return Ok(new { id, message = "Reconciliation deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete reconciliation {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ONE LINE BY HAND
    // ══════════════════════════════════════════════════════════════════

    public record LineRequest(DateOnly? Date, string? Description, string? Reference, decimal Amount);

    /// <summary>
    /// One statement line typed in -- a bank charge seen on the paper
    /// statement, a line the export left out. Same checks as an imported row.
    /// </summary>
    [HttpPost("{id:int}/lines")]
    public async Task<IActionResult> AddLine(int id, [FromBody] LineRequest body)
    {
        try
        {
            var recon = await _db.BankReconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });
            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is finalised and cannot be changed." });

            var errors = CheckLine(recon, body.Date, body.Description, body.Reference, body.Amount);
            if (errors.Count > 0) return BadRequest(new { message = errors[0] });

            var line = new BankStatementLine
            {
                ReconciliationId = id,
                LineDate = body.Date!.Value,
                Description = body.Description!.Trim(),
                Reference = string.IsNullOrWhiteSpace(body.Reference) ? null : body.Reference.Trim(),
                Amount = Math.Round(body.Amount, 2)
            };
            _db.BankStatementLines.Add(line);
            await _db.SaveChangesAsync();
            await Log("STATEMENT_LINE_ADDED", "BankReconciliation", id.ToString(),
                $"{line.LineDate:yyyy-MM-dd} {line.Description} {line.Amount:N2}", 1);

            return Ok(new { id = line.StatementLineId, message = "Statement line added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"add a line to reconciliation {id}");
        }
    }

    /// <summary>
    /// Removes a statement line that was loaded by mistake. A MATCHED line is
    /// refused: take the match off first, so nobody deletes a pairing without
    /// seeing it.
    /// </summary>
    [HttpDelete("{id:int}/lines/{lineId:int}")]
    public async Task<IActionResult> DeleteLine(int id, int lineId)
    {
        try
        {
            var recon = await _db.BankReconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });
            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is finalised and cannot be changed." });

            var line = await _db.BankStatementLines.FirstOrDefaultAsync(l => l.StatementLineId == lineId && l.ReconciliationId == id);
            if (line is null) return NotFound(new { message = "That line is not on this reconciliation." });
            if (line.MatchedLineId is not null)
                return BadRequest(new { message = "This line is matched. Remove the match first." });

            _db.BankStatementLines.Remove(line);
            await _db.SaveChangesAsync();
            await Log("STATEMENT_LINE_DELETED", "BankReconciliation", id.ToString(),
                $"{line.LineDate:yyyy-MM-dd} {line.Description} {line.Amount:N2}", 1);

            return Ok(new { id = lineId, message = "Statement line removed." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"remove a line from reconciliation {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  IMPORT A STATEMENT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The sheet to fill in, for a bank whose download is not already in a
    /// shape the import reads. Deposit and Withdrawal are separate columns
    /// because that is how every bank statement prints them.
    /// </summary>
    [HttpGet("import/template")]
    public IActionResult ImportTemplate()
    {
        try
        {
            var columns = new[]
            {
                new XlsxWriter.Column("Date", "date", XlsxWriter.CellKind.Text, 12),
                new XlsxWriter.Column("Description", "description", XlsxWriter.CellKind.Text, 44),
                new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Deposit", "deposit", XlsxWriter.CellKind.Money, 16),
                new XlsxWriter.Column("Withdrawal", "withdrawal", XlsxWriter.CellKind.Money, 16),
            };
            var sample = JsonSerializer.SerializeToElement(new object[]
            {
                new { date = "01/09/2026", description = "Cash deposit - counter", reference = "DP-1001", deposit = 150000m, withdrawal = (decimal?)null },
                new { date = "02/09/2026", description = "Cheque 004512 - supplier", reference = "004512", deposit = (decimal?)null, withdrawal = 82500m },
            });
            var bytes = XlsxWriter.FromJson("Statement", sample, columns);
            return File(bytes, XlsxWriter.ContentType, "bank-statement-template.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the statement template");
        }
    }

    /// <summary>
    /// Reads the uploaded statement and checks every row WITHOUT writing
    /// anything. The screen shows the result -- what each row will become, and
    /// what the statement will add up to once it is in -- and Commit sends the
    /// good rows back.
    ///
    /// Bank downloads are not tidy: a few lines of account title and address
    /// above the table, a balance column, a totals row at the bottom. So the
    /// header row is FOUND (the first row, within the first 30, that names a
    /// date and an amount), columns are matched by name in any order, and a
    /// row with no date and no amount -- a totals line, a blank -- is skipped
    /// rather than reported.
    /// </summary>
    [HttpPost("{id:int}/import/preview")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> ImportPreview(int id, IFormFile? file)
    {
        try
        {
            var recon = await _db.BankReconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });
            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is finalised and cannot be changed." });

            if (file is null || file.Length == 0) return BadRequest(new { message = "Choose the statement file to import." });
            var isXlsx = file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
            var isCsv = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            if (!isXlsx && !isCsv)
                return BadRequest(new { message = "Only .xlsx or .csv files can be imported. An old .xls must be saved as an Excel Workbook first." });

            List<string[]> sheet;
            await using (var s = file.OpenReadStream())
            {
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                ms.Position = 0;
                try { sheet = isXlsx ? XlsxReader.ReadFirstSheet(ms) : CsvReader.Read(ms); }
                catch (Exception) { return BadRequest(new { message = $"That file could not be read as {(isXlsx ? "an Excel workbook" : "a CSV file")}." }); }
            }

            var layout = FindLayout(sheet);
            if (layout is null)
                return BadRequest(new
                {
                    message = "No header row was found. The statement needs a Date column, a Description column, and either " +
                              "an Amount column or Deposit and Withdrawal (Credit and Debit) columns. Download the template to see the shape."
                });

            var parsed = new List<ImportRow>();
            for (var i = layout.HeaderRow + 1; i < sheet.Count; i++)
            {
                var r = sheet[i];
                var dateText = Cell(r, layout.Date);
                var desc = Cell(r, layout.Description);
                var reference = Cell(r, layout.Reference);
                var amountText = Cell(r, layout.Amount);
                var inText = Cell(r, layout.In);
                var outText = Cell(r, layout.Out);

                /* Blank rows and a totals / closing-balance line: no date and
                   nothing that reads as a date. Skipped silently. */
                if (string.IsNullOrWhiteSpace(dateText) && string.IsNullOrWhiteSpace(desc)) continue;
                if (string.IsNullOrWhiteSpace(dateText) &&
                    Regex.IsMatch(desc, @"^(total|closing|opening|balance)", RegexOptions.IgnoreCase)) continue;

                var errors = new List<string>();
                var date = ParseDate(dateText);
                if (date is null) errors.Add(string.IsNullOrWhiteSpace(dateText) ? "The date is missing." : $"\"{dateText}\" is not a date.");

                decimal amount = 0;
                if (layout.Amount >= 0)
                {
                    var a = ParseAmount(amountText);
                    if (a is null) errors.Add($"\"{amountText}\" is not an amount.");
                    else amount = a.Value;
                }
                else
                {
                    var inn = string.IsNullOrWhiteSpace(inText) ? 0m : ParseAmount(inText);
                    var outt = string.IsNullOrWhiteSpace(outText) ? 0m : ParseAmount(outText);
                    if (inn is null) errors.Add($"\"{inText}\" is not an amount.");
                    if (outt is null) errors.Add($"\"{outText}\" is not an amount.");
                    if (inn is not null && outt is not null)
                    {
                        if (inn != 0 && outt != 0) errors.Add("A row can be a deposit or a withdrawal, not both.");
                        /* Withdrawals are sometimes printed negative already;
                           the sign of the COLUMN decides, not the figure. */
                        amount = Math.Abs(inn.Value) - Math.Abs(outt.Value);
                    }
                }

                parsed.Add(new ImportRow(i + 1, date, desc, string.IsNullOrWhiteSpace(reference) ? null : reference,
                    Math.Round(amount, 2), errors, new List<string>()));
                if (parsed.Count > MaxImportRows)
                    return BadRequest(new { message = $"At most {MaxImportRows:N0} rows can be imported at once." });
            }

            if (parsed.Count == 0) return BadRequest(new { message = "The file has a header row but no statement lines under it." });

            var rows = await Validate(recon, parsed);
            return Ok(Summarise(recon, rows, new
            {
                header = layout.HeaderRow + 1,
                date = Header(sheet, layout, layout.Date),
                description = Header(sheet, layout, layout.Description),
                reference = Header(sheet, layout, layout.Reference),
                amount = Header(sheet, layout, layout.Amount),
                deposit = Header(sheet, layout, layout.In),
                withdrawal = Header(sheet, layout, layout.Out),
            }, await ExistingMovement(id)));
        }
        catch (Exception ex)
        {
            return Fail(ex, $"read a statement for reconciliation {id}");
        }
    }

    public record CommitRow(int Row, DateOnly? Date, string? Description, string? Reference, decimal Amount);
    public record CommitRequest(List<CommitRow>? Rows);

    /// <summary>
    /// Writes the rows the preview passed, re-checked here -- the preview is
    /// the browser's copy and proves nothing. All or nothing: one bad row stops
    /// the import with its row number, so a statement is never half loaded.
    /// Rows the preview only WARNED about (already on the statement, say) are
    /// the screen's to leave out; the screen leaves them out by default.
    /// </summary>
    [HttpPost("{id:int}/import/commit")]
    public async Task<IActionResult> ImportCommit(int id, [FromBody] CommitRequest body)
    {
        try
        {
            var recon = await _db.BankReconciliations.AsNoTracking().FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });
            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is finalised and cannot be changed." });

            var input = body.Rows ?? new List<CommitRow>();
            if (input.Count == 0) return BadRequest(new { message = "Nothing to import." });
            if (input.Count > MaxImportRows) return BadRequest(new { message = $"At most {MaxImportRows:N0} rows can be imported at once." });

            var rows = await Validate(recon, input.Select(r => new ImportRow(
                r.Row, r.Date, r.Description ?? "", r.Reference, Math.Round(r.Amount, 2),
                new List<string>(), new List<string>())).ToList());

            var bad = rows.FirstOrDefault(r => r.Errors.Count > 0);
            if (bad is not null)
                return BadRequest(new { message = $"Row {bad.Row}: {bad.Errors[0]} Nothing was imported." });

            foreach (var r in rows)
            {
                _db.BankStatementLines.Add(new BankStatementLine
                {
                    ReconciliationId = id,
                    LineDate = r.Date!.Value,
                    Description = r.Description.Trim(),
                    Reference = r.Reference?.Trim(),
                    Amount = r.Amount
                });
            }
            /* One SaveChanges is one transaction: every row or none. */
            await _db.SaveChangesAsync();

            var net = rows.Sum(r => r.Amount);
            await Log("STATEMENT_IMPORTED", "BankReconciliation", id.ToString(),
                $"{rows.Count} lines, net {net:N2}", 1);

            return Ok(new
            {
                id,
                imported = rows.Count,
                net,
                message = $"{rows.Count} statement {(rows.Count == 1 ? "line" : "lines")} imported."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"import a statement into reconciliation {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CHECKS
    // ══════════════════════════════════════════════════════════════════

    private sealed record ImportRow(
        int Row, DateOnly? Date, string Description, string? Reference, decimal Amount,
        List<string> Errors, List<string> Warnings);

    /// <summary>The header checks shared by Create and Update. Null when fine.</summary>
    private async Task<string?> CheckHeader(ReconciliationRequest body, int? exceptId)
    {
        if (body.StatementDate is not DateOnly to) return "The statement's end date is required.";
        if (body.PeriodFrom is not DateOnly from) return "The statement's start date is required.";
        if (from > to) return "The statement cannot end before it starts.";
        if (to > Today()) return "A statement can only cover days that have happened. The end date is in the future.";
        if (to.DayNumber - from.DayNumber > 400) return "A statement longer than a year cannot be reconciled in one go. Split it by month or quarter.";

        var account = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountId == body.AccountId)
            .Select(a => new { a.IsActive, a.IsGroup, type = a.AccountType.TypeName, a.AccountName })
            .FirstOrDefaultAsync();
        if (account is null) return "Pick the bank account.";
        if (account.type != CashAndBankType) return $"{account.AccountName} is not a Cash & Bank account.";
        if (account.IsGroup) return $"{account.AccountName} is a group heading. Pick one account under it.";
        if (!account.IsActive) return $"{account.AccountName} is switched off.";

        /* Overlap with this account's other statements. Rows from before
           migration 41 have no start date; they are treated as covering only
           their end date, which is all that is known about them. */
        var clash = await _db.BankReconciliations.AsNoTracking()
            .Where(r => r.AccountId == body.AccountId && r.ReconciliationId != (exceptId ?? 0)
                     && (r.PeriodFrom ?? r.StatementDate) <= to && r.StatementDate >= from)
            .OrderBy(r => r.StatementDate)
            .Select(r => new { r.PeriodFrom, r.StatementDate })
            .FirstOrDefaultAsync();
        if (clash is not null)
            return clash.PeriodFrom is DateOnly cf
                ? $"{account.AccountName} already has a statement for {cf:dd MMM yyyy} – {clash.StatementDate:dd MMM yyyy}, which overlaps these dates."
                : $"{account.AccountName} already has a statement ending {clash.StatementDate:dd MMM yyyy}, inside these dates.";

        return null;
    }

    /// <summary>
    /// A bank statement opens on the balance the previous one closed on. When
    /// this one does not, the reconciliation is still started -- the previous
    /// one may be the one that is wrong -- but the screen says so.
    /// </summary>
    private async Task<string?> ContinuityWarning(BankReconciliation recon)
    {
        var prev = await _db.BankReconciliations.AsNoTracking()
            .Where(r => r.AccountId == recon.AccountId && r.StatementDate < (recon.PeriodFrom ?? recon.StatementDate))
            .OrderByDescending(r => r.StatementDate)
            .Select(r => new { r.StatementDate, r.ClosingBalance })
            .FirstOrDefaultAsync();
        if (prev is null || Math.Abs(prev.ClosingBalance - recon.OpeningBalance) < 0.01m) return null;
        return $"The statement to {prev.StatementDate:dd MMM yyyy} closed at {prev.ClosingBalance:N2}, " +
               $"but this one opens at {recon.OpeningBalance:N2}. Check both against the bank's paper.";
    }

    /// <summary>The checks one line has to pass, typed in or imported. Empty when fine.</summary>
    private static List<string> CheckLine(BankReconciliation recon, DateOnly? date, string? description, string? reference, decimal amount)
    {
        var errors = new List<string>();
        if (date is not DateOnly d) errors.Add("The date is missing.");
        else if (recon.PeriodFrom is DateOnly from && (d < from || d > recon.StatementDate))
            errors.Add($"{d:dd MMM yyyy} is outside this statement ({from:dd MMM yyyy} – {recon.StatementDate:dd MMM yyyy}).");
        else if (recon.PeriodFrom is null && d > recon.StatementDate)
            errors.Add($"{d:dd MMM yyyy} is after this statement ends ({recon.StatementDate:dd MMM yyyy}).");

        if (string.IsNullOrWhiteSpace(description)) errors.Add("The description is missing.");
        else if (description.Trim().Length > 200) errors.Add("The description is longer than 200 characters.");
        if (reference is not null && reference.Trim().Length > 60) errors.Add("The reference is longer than 60 characters.");

        /* The table refuses a zero line (CHECK "Amount" <> 0): a line that moves
           no money has nothing to reconcile. */
        if (amount == 0) errors.Add("The amount is zero.");
        else if (Math.Abs(amount) >= 1_000_000_000_000m) errors.Add("The amount is too large.");
        return errors;
    }

    /// <summary>
    /// Every row's own checks, plus two WARNINGS that need the whole picture:
    /// a row already on this statement (the same file loaded twice), and a row
    /// that appears twice in the file. Warnings do not stop an import -- two
    /// identical bank charges on one day are real -- but the screen leaves
    /// such rows unticked.
    /// </summary>
    private async Task<List<ImportRow>> Validate(BankReconciliation recon, List<ImportRow> rows)
    {
        var existing = await _db.BankStatementLines.AsNoTracking()
            .Where(l => l.ReconciliationId == recon.ReconciliationId)
            .Select(l => new { l.LineDate, l.Amount, l.Description, l.Reference })
            .ToListAsync();

        static string Key(DateOnly d, decimal a, string? reference, string desc) =>
            $"{d:yyyyMMdd}|{a:0.00}|{(string.IsNullOrWhiteSpace(reference) ? desc.Trim().ToLowerInvariant() : reference.Trim().ToLowerInvariant())}";

        var onFile = existing.Select(l => Key(l.LineDate, l.Amount, l.Reference, l.Description)).ToHashSet();
        var seen = new HashSet<string>();

        foreach (var r in rows)
        {
            /* The preview has already said "not a date" for a date it could not
               read; "the date is missing" on top of that says it twice. */
            var dateAlreadyReported = r.Errors.Any(e => e.EndsWith("is not a date."));
            r.Errors.AddRange(CheckLine(recon, r.Date, r.Description, r.Reference, r.Amount)
                .Where(e => !r.Errors.Contains(e) && !(dateAlreadyReported && e == "The date is missing.")));
            if (r.Date is not DateOnly d || r.Amount == 0) continue;

            var key = Key(d, r.Amount, r.Reference, r.Description ?? "");
            if (onFile.Contains(key)) r.Warnings.Add("Already on this statement.");
            if (!seen.Add(key)) r.Warnings.Add("Appears twice in this file.");
        }
        return rows;
    }

    /// <summary>What the lines already on the statement move, so the preview can say where the import leaves it.</summary>
    private async Task<decimal> ExistingMovement(int id) =>
        await _db.BankStatementLines.Where(l => l.ReconciliationId == id).SumAsync(l => (decimal?)l.Amount) ?? 0m;

    private static object Summarise(BankReconciliation recon, List<ImportRow> rows, object columns, decimal existing)
    {
        var good = rows.Where(r => r.Errors.Count == 0).ToList();
        var clean = good.Where(r => r.Warnings.Count == 0).ToList();
        var expected = recon.ClosingBalance - recon.OpeningBalance;
        var after = existing + clean.Sum(r => r.Amount);
        return new
        {
            columns,
            rows = rows.Select(r => new
            {
                row = r.Row, date = r.Date, description = r.Description, reference = r.Reference,
                amount = r.Amount, errors = r.Errors, warnings = r.Warnings
            }),
            summary = new
            {
                total = rows.Count,
                valid = good.Count,
                withErrors = rows.Count - good.Count,
                withWarnings = good.Count - clean.Count,
                moneyIn = good.Where(r => r.Amount > 0).Sum(r => r.Amount),
                moneyOut = good.Where(r => r.Amount < 0).Sum(r => -r.Amount),
                /* Where the statement stands if the clean rows go in: the
                   balances say the lines should move `expected`; finalise
                   refuses until `unexplained` is zero. */
                existingMovement = existing,
                expectedMovement = expected,
                movementAfter = after,
                unexplainedAfter = expected - after
            }
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  READING A BANK'S FILE
    // ══════════════════════════════════════════════════════════════════

    private sealed record Layout(int HeaderRow, int Date, int Description, int Reference, int Amount, int In, int Out);

    private static readonly string[] DateNames =
        { "date", "txn date", "transaction date", "value date", "posting date", "tran date", "booking date", "trans date" };
    private static readonly string[] DescriptionNames =
        { "description", "narration", "particulars", "details", "transaction details", "remarks", "memo", "transaction description" };
    private static readonly string[] ReferenceNames =
        { "reference", "ref", "ref no", "reference no", "reference number", "cheque no", "chq no", "cheque", "cheque number", "instrument no", "transaction id", "txn id" };
    private static readonly string[] AmountNames =
        { "amount", "net amount", "transaction amount", "amount pkr" };
    private static readonly string[] InNames =
        { "deposit", "deposits", "credit", "credits", "credit amount", "cr", "money in", "paid in", "receipts", "deposit amount" };
    private static readonly string[] OutNames =
        { "withdrawal", "withdrawals", "debit", "debits", "debit amount", "dr", "money out", "paid out", "payments", "withdrawal amount" };

    private static string Norm(string h) =>
        Regex.Replace(h.Trim().ToLowerInvariant().Replace('.', ' ').Replace('_', ' ').Replace("(", " ").Replace(")", " "), @"\s+", " ").Trim();

    private static Layout? FindLayout(List<string[]> sheet)
    {
        for (var i = 0; i < Math.Min(30, sheet.Count); i++)
        {
            var head = sheet[i].Select(Norm).ToList();
            int Col(string[] names) => head.FindIndex(names.Contains);
            var date = Col(DateNames);
            var desc = Col(DescriptionNames);
            var amount = Col(AmountNames);
            var inn = Col(InNames);
            var outt = Col(OutNames);
            if (date < 0 || desc < 0) continue;
            if (amount < 0 && (inn < 0 || outt < 0)) continue;
            /* Deposit + Withdrawal win over a lone Amount column: when a bank
               prints both, the Amount column is usually unsigned. */
            if (inn >= 0 && outt >= 0) amount = -1;
            return new Layout(i, date, desc, Col(ReferenceNames), amount, inn, outt);
        }
        return null;
    }

    private static string? Header(List<string[]> sheet, Layout l, int col) =>
        col < 0 ? null : sheet[l.HeaderRow][col];

    private static string Cell(string[] row, int col) => col >= 0 && col < row.Length ? row[col].Trim() : "";

    private static readonly string[] DateFormats =
    {
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yy", "d-MMM-yy", "dd MMM yy",
        "MMM dd, yyyy", "MMM d, yyyy", "dd/MM/yy", "d/M/yy",
    };

    /// <summary>
    /// A statement date. Day first (dd/MM/yyyy) -- that is how Pakistani banks
    /// print it, so 03/09/2026 is 3 September, never 9 March. An .xlsx date
    /// cell arrives as Excel's day count (46266), which is turned back into a
    /// date; a time on the end ("01/09/2026 10:32") is dropped.
    /// </summary>
    private static DateOnly? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();

        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
            return DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));

        t = Regex.Replace(t, @"\s+\d{1,2}:\d{2}(:\d{2})?(\s*[AaPp][Mm])?$", "");
        t = t.Replace("T00:00:00", "");
        return DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)
            ? d : null;
    }

    /// <summary>
    /// An amount as a bank prints it: "1,50,000.00", "PKR 2,500", "(82,500.00)"
    /// for money out, "82,500.00 DR" / "150,000 CR" in a single signed column.
    /// </summary>
    private static decimal? ParseAmount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant();
        var sign = 1m;
        if (t.StartsWith('(') && t.EndsWith(')')) { sign = -1m; t = t[1..^1]; }
        if (t.EndsWith("DR")) { sign = -1m; t = t[..^2]; }
        else if (t.EndsWith("CR")) { t = t[..^2]; }
        t = t.Replace("PKR", "").Replace("RS.", "").Replace("RS", "").Replace(",", "").Replace(" ", "");
        if (t.EndsWith('-')) { sign = -sign; t = t[..^1]; }
        return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
            ? sign * v : null;
    }
}
