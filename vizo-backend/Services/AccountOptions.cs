using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// EVERY ACCOUNT, GROUPED, for the "Paid from" and "Expense head" pickers (the
/// owner, 8 Nov: "pehla Cash on Hand, Meezan and Faysal hi ayega, likin jitna
/// bhi customers and pora db ma jitna bhi accounts honga sab ana chahye ...
/// category group by krke").
///
/// One option is an account, or a person ON a control account:
///   a:{accountId}   a postable account of the chart
///   p:{partyId}     a customer (on 1130 Accounts Receivable) or a supplier
///                   (on 2101 Accounts Payable) -- the line carries PartyUserId
///   s:{staffId}     a member of staff (on 2140 Staff Payables) -- StaffId
///
/// Groups, in order: Cash &amp; Bank (Cash on Hand, Meezan, Faysal first),
/// Customers, Suppliers, Staff, then the chart's groups. With expensesFirst
/// the Expenses group leads instead, for an expense head.
/// </summary>
public static class AccountOptions
{
    public sealed record Option(string Key, string Label, string Group, int AccountId,
        int? PartyId, int? StaffId, string Hint);

    public const string GroupCashBank = "Cash & Bank";
    public const string GroupCustomers = "Customers";
    public const string GroupSuppliers = "Suppliers";
    public const string GroupStaff = "Staff";

    private static readonly string[] Leading = { LedgerPosting.CashOnHandCode, "1111", "1113" };
    private static readonly string[] ChartGroups = { "Expenses", "Assets", "Liabilities", "Capital", "Revenue" };

    public static async Task<List<Option>> AllAsync(AppDbContext db, bool expensesFirst)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.IsActive && !a.IsGroup)
            .OrderBy(a => a.AccountCode)
            .Select(a => new { a.AccountId, a.AccountCode, a.AccountName, type = a.AccountType.TypeName, group = a.AccountType.Group.GroupName })
            .ToListAsync();
        var ar = accounts.FirstOrDefault(a => a.AccountCode == LedgerPosting.ReceivableCode);
        var ap = accounts.FirstOrDefault(a => a.AccountCode == "2101");
        var staffAcc = accounts.FirstOrDefault(a => a.AccountCode == LedgerPosting.StaffPayablesCode);

        var cash = accounts.Where(a => a.type == "Cash & Bank")
            .OrderBy(a => Array.IndexOf(Leading, a.AccountCode) is var i && i >= 0 ? i : 99).ThenBy(a => a.AccountCode)
            .Select(a => new Option($"a:{a.AccountId}", a.AccountName, GroupCashBank, a.AccountId, null, null, a.AccountCode))
            .ToList();

        var parties = await db.Parties.AsNoTracking()
            .Where(p => p.User.IsActive)
            .OrderBy(p => p.DisplayName ?? p.LegalName)
            .Select(p => new { p.UserId, p.PartyCode, name = p.DisplayName ?? p.LegalName, role = p.User.RoleId })
            .ToListAsync();
        /* Role 6 is a supplier, 7 both, anything else a customer -- the same
           reading as AccountingController.Lookups. "Both" is a customer here. */
        var customers = ar is null ? new List<Option>() : parties.Where(p => p.role != 6)
            .Select(p => new Option($"p:{p.UserId}", p.name, GroupCustomers, ar.AccountId, p.UserId, null, $"{p.PartyCode} · on {ar.AccountCode}"))
            .ToList();
        var suppliers = ap is null ? new List<Option>() : parties.Where(p => p.role == 6)
            .Select(p => new Option($"p:{p.UserId}", p.name, GroupSuppliers, ap.AccountId, p.UserId, null, $"{p.PartyCode} · on {ap.AccountCode}"))
            .ToList();

        var staff = staffAcc is null ? new List<Option>() : (await db.StaffMembers.AsNoTracking()
                .Where(s => s.IsActive).OrderBy(s => s.FullName)
                .Select(s => new { s.StaffId, s.StaffCode, s.FullName }).ToListAsync())
            .Select(s => new Option($"s:{s.StaffId}", s.FullName, GroupStaff, staffAcc.AccountId, null, s.StaffId, $"{s.StaffCode} · on {staffAcc.AccountCode}"))
            .ToList();

        var chart = ChartGroups.SelectMany(g => accounts.Where(a => a.group == g && a.type != "Cash & Bank")
                .Select(a => new Option($"a:{a.AccountId}", a.AccountName, g, a.AccountId, null, null, a.AccountCode)))
            .ToList();

        if (!expensesFirst) return cash.Concat(customers).Concat(suppliers).Concat(staff).Concat(chart).ToList();

        var expenses = chart.Where(o => o.Group == "Expenses").ToList();
        return expenses.Concat(cash).Concat(customers).Concat(suppliers).Concat(staff)
            .Concat(chart.Where(o => o.Group != "Expenses")).ToList();
    }

    /// <summary>
    /// Checks one picked side: a postable account, and -- when a customer,
    /// supplier or member of staff is named -- that it is on the right
    /// control account. Returns the reason it is refused, or null.
    /// </summary>
    public static async Task<string?> CheckAsync(AppDbContext db, int accountId, int? partyId, int? staffId)
    {
        if (partyId is not null && staffId is not null) return "Pick one person, not both a party and a member of staff.";
        var acc = await db.Accounts.AsNoTracking().Where(a => a.AccountId == accountId)
            .Select(a => new { a.AccountCode, a.IsGroup, a.IsActive }).FirstOrDefaultAsync();
        if (acc is null || acc.IsGroup || !acc.IsActive) return "Pick an open account that takes postings.";
        if (partyId is int pid)
        {
            if (acc.AccountCode is not (LedgerPosting.ReceivableCode or "2101"))
                return "A customer or supplier sits on Accounts Receivable or Accounts Payable.";
            if (!await db.Parties.AnyAsync(p => p.UserId == pid)) return "That customer or supplier does not exist.";
        }
        if (staffId is int sid)
        {
            if (acc.AccountCode != LedgerPosting.StaffPayablesCode) return "A member of staff sits on 2140 Staff Payables.";
            if (!await db.StaffMembers.AnyAsync(s => s.StaffId == sid)) return "That member of staff does not exist.";
        }
        return null;
    }
}
