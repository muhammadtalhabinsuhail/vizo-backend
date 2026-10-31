using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// Opening an account in the chart from somewhere other than the Account List
/// -- a vendor from the expense sheet, a salary account from the staff ledger
/// (the owner, 1 Nov). The code is the next free number under the heading, the
/// way the Account List itself suggests one: 2171, 2172 ... under 2170.
/// </summary>
public static class ChartAccounts
{
    /// <summary>Opens a postable account under the heading with this code, typed like the heading.</summary>
    public static async Task<(Account? Account, string? Error)> OpenUnderAsync(
        AppDbContext db, string parentCode, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 100) return (null, "An account name is 2 to 100 characters.");

        var parent = await db.Accounts.FirstOrDefaultAsync(a => a.AccountCode == parentCode && a.IsGroup);
        if (parent is null) return (null, $"Heading {parentCode} is not in the chart -- run the latest migration.");

        if (await db.Accounts.AnyAsync(a => a.ParentAccountId == parent.AccountId && a.AccountName.ToLower() == name.ToLower()))
            return (null, $"{name} is already an account under {parent.AccountName}.");

        var account = new Account
        {
            AccountCode = await NextChildCodeAsync(db, parent),
            AccountName = name,
            ParentAccountId = parent.AccountId,
            AccountTypeId = parent.AccountTypeId,
            IsGroup = false,
            OpeningBalance = 0,
            CurrencyCode = "PKR",
            IsActive = true
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return (account, null);
    }

    /// <summary>The next unused numeric code under a heading.</summary>
    public static async Task<string> NextChildCodeAsync(AppDbContext db, Account parent)
    {
        var codes = await db.Accounts.AsNoTracking()
            .Where(a => a.ParentAccountId == parent.AccountId)
            .Select(a => a.AccountCode).ToListAsync();
        var used = (await db.Accounts.AsNoTracking().Select(a => a.AccountCode).ToListAsync()).ToHashSet();
        var start = codes.Select(c => long.TryParse(c, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        if (start == 0) start = long.TryParse(parent.AccountCode, out var p) ? p : 0;
        var next = start + 1;
        while (used.Contains(next.ToString())) next++;
        return next.ToString();
    }
}
