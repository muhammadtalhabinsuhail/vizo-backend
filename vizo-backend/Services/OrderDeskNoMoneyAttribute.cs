using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace vizo_backend.Services;

/// <summary>
/// Takes money and purchases out of a controller's answers when the caller is
/// the Order Department.
///
/// ─────────────────────────────── WHY A FILTER ────────────────────────────────
///
/// The owner, 26 September: the order desk keeps Stock in Hand, Transfers,
/// Stock Correction and Stock History, and has "no money, accounts or purchases
/// anywhere ... no purchases or cost prices". Enforced on the API by role.
///
/// Stock History (ProductHistoryController) is 1,500 lines that read every
/// document a product ever touched -- supplier bills, goods receipts at cost,
/// invoices at the rep's price, a summary with gross profit -- and the same
/// file is where the purchases branch (session A) is working the same week.
/// Threading a role check through every one of its projections would touch
/// dozens of lines both branches need. This attribute touches ONE line of it
/// -- the attribute on the class -- and does the rest on the way out:
///
///   · events and chips in the "purchasing" group are dropped (purchases);
///   · a movement's document is dropped when it is a supplier's (it names one);
///   · every money field -- cost, duty, margin, rate, amount, value, profit,
///     sales -- is written as 0, so a screen still renders its layout and
///     simply has no figure to show;
///   · a file export is refused outright: it is a spreadsheet of all of it.
///
/// Every other role passes through untouched -- nothing is re-serialised for
/// them, so they pay nothing for this.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OrderDeskNoMoneyAttribute : Attribute, IAsyncResultFilter
{
    private const string Role = "order-dept";

    /// <summary>Property names that carry money, compared case-insensitively.</summary>
    private static readonly HashSet<string> MoneyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "costPrice", "dutyPrice", "marginPrice", "unitCost", "cost", "landed", "landedCost",
        "rate", "amount", "value", "valueAtCost", "stockValue", "lineTotal", "total", "subtotal",
        "purchasedValue", "netSales", "billedWithTax", "costOfSales", "grossProfit",
        "averageSellingPrice", "marginPercent", "price", "salePrice", "unitPrice", "tax", "discount"
    };

    /// <summary>Purchase facts that are not money but are "purchases" all the same.</summary>
    private static readonly HashSet<string> PurchaseKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "purchasedUnits", "damagedOnArrival", "returnedToSuppliers"
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!context.HttpContext.User.IsInRole(Role))
        {
            await next();
            return;
        }

        switch (context.Result)
        {
            case FileResult:
                context.Result = new ObjectResult(new
                {
                    message = "The Order Department does not export this -- it carries costs and purchases."
                }) { StatusCode = 403 };
                break;

            case ObjectResult { Value: not null } ok when (ok.StatusCode ?? 200) < 300:
                var node = JsonSerializer.SerializeToNode(ok.Value, ok.Value.GetType(), Json);
                ok.Value = Scrub(node, 0);
                ok.DeclaredType = typeof(JsonNode);
                break;
        }

        await next();
    }

    /* depth: "total" at the top of a list answer is a COUNT (total, page,
       pageSize), not money -- it is only treated as money further down. */
    private static JsonNode? Scrub(JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonArray arr:
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    if (IsPurchasing(arr[i])) { arr.RemoveAt(i); continue; }
                    Scrub(arr[i], depth + 1);
                }
                return arr;

            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var v = obj[key];
                    if (key.Equals("lastPurchasedOn", StringComparison.OrdinalIgnoreCase)) { obj[key] = null; continue; }
                    if (key.Equals("document", StringComparison.OrdinalIgnoreCase) && v is JsonObject d && d.ContainsKey("supplier"))
                    {
                        obj[key] = null;
                        continue;
                    }
                    if (v is JsonValue val && val.TryGetValue<decimal>(out _) &&
                        (MoneyKeys.Contains(key) || PurchaseKeys.Contains(key)) &&
                        !(depth == 0 && key.Equals("total", StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[key] = 0;
                        continue;
                    }
                    Scrub(v, depth + 1);
                }
                return obj;

            default:
                return node;
        }
    }

    /// <summary>A timeline event or a filter chip that belongs to purchasing.</summary>
    private static bool IsPurchasing(JsonNode? n) =>
        n is JsonObject o &&
        ((o["group"] is JsonValue g && g.TryGetValue<string>(out var gs) && gs == "purchasing") ||
         (o["key"] is JsonValue k && k.TryGetValue<string>(out var ks) && ks == "purchasing"));
}
