using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// The few pieces every report controller needs and ReportsController keeps
/// private: the letterhead off the Company row, and the JSON shape the .xlsx
/// writer reads. Static, no DI, nothing to register -- the same as
/// LedgerPosting.
///
/// Why not reuse ReportsController's copies: that file is 2,500 lines that
/// three branches touch, and its helpers are private. The report controllers
/// added on 27 Sep (SalesReportsController, PurchaseReportsController) are new
/// files, per the rule that new endpoints go in new controllers; they share
/// this instead of each growing a third copy.
/// </summary>
public static class ReportKit
{
    /// <summary>Anonymous objects serialise with their own (already camelCase) names; cycles are cut.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, value.GetType(), Json);

    /// <summary>The company letterhead every printed report carries.</summary>
    public static async Task<DocumentPdf.LetterHead> LetterHeadAsync(AppDbContext db)
    {
        var c = await db.Companies.AsNoTracking()
            .Select(x => new
            {
                x.CompanyName, x.LegalName, x.AddressLine,
                city = x.City.CityName,
                x.Country, x.Phone, x.Email, x.Ntn, x.Strn, x.CurrencySymbol
            })
            .FirstOrDefaultAsync();

        return new DocumentPdf.LetterHead(
            c?.CompanyName ?? "AdvPOS",
            c?.LegalName ?? c?.CompanyName ?? "AdvPOS",
            c?.AddressLine ?? "", c?.city ?? "", c?.Country ?? "",
            c?.Phone ?? "", c?.Email ?? "", c?.Ntn ?? "", c?.Strn ?? "",
            c?.CurrencySymbol ?? "PKR");
    }

    /// <summary>A money cell that reads "-" rather than a column of 0.00.</summary>
    public static string Zero(decimal v) => v == 0 ? "-" : DocumentPdf.Money(v);

    /// <summary>A default reporting window: the first of this month to today.</summary>
    public static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to)
    {
        var today = BusinessClock.Today();
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1);
        return start > end ? (end, start) : (start, end);
    }
}
