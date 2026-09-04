namespace vizo_backend.Services;

/// <summary>
/// The FIXED list of reports the question-answering box is allowed to reach.
///
/// ─────────────────────────── WHY A LIST AND NOT SQL ────────────────────────
///
/// The obvious way to answer "is anybody about to stop buying from us" is to
/// let the model write a query. Do not. A model that can write SQL will one day
/// write a DELETE, or a SELECT that reads a table it was never meant to see,
/// and it will do it while sounding completely reasonable.
///
/// So it does not get a database. It gets this menu. Its whole job is to pick
/// the right line and fill in the parameters; the endpoint behind that line is
/// one a person could have opened themselves, with the same permission checks.
/// The worst a wrong answer can do is show the wrong report.
///
/// Adding a report here is how the box learns to answer a new question.
/// </summary>
public static class ReportCatalogue
{
    public record Entry(
        string Key,
        string Route,
        string Answers,
        string Parameters,
        string Screen);

    public static readonly IReadOnlyList<Entry> All = new List<Entry>
    {
        new("sales-drop",
            "/api/reports/sales-drop/explain",
            "Why sales went up or down between two periods. Which customers bought less or "
          + "stopped, which products fell, what was out of stock, whether discounting changed, "
          + "which rep's numbers dropped, which costs rose.",
            "from, to, baseFrom, baseTo (all yyyy-MM-dd; omit for this month vs last)",
            "/reports/sales-summary"),

        new("recovery-priority",
            "/api/reports/recovery-priority",
            "Who owes money and who to telephone first. Aging, payment history, credit limits.",
            "none",
            "/reports/aging/customer"),

        new("at-risk",
            "/api/reports/customers/at-risk",
            "Customers who have gone quiet or whose orders are shrinking.",
            "take (how many, default 15)",
            "/parties/customers"),

        new("demand-forecast",
            "/api/reports/demand-forecast",
            "What to reorder and how much, from past sales and current stock.",
            "lookbackDays, coverDays, take",
            "/inventory/stock"),

        new("dead-stock",
            "/api/reports/dead-stock/advice",
            "Stock that is not selling and how much cash is stuck in it.",
            "days (the window, default 90)",
            "/reports/dead-stock"),

        new("margin-watch",
            "/api/reports/margin-watch",
            "Products selling on a thin or negative margin, and what was really charged.",
            "thinBelowPercent, days",
            "/inventory/products"),

        new("month-end",
            "/api/reports/month-end-summary",
            "A whole month summarised: revenue, top customers and products, expenses, "
          + "compared with the month before.",
            "year, month (numbers; omit for the current month)",
            "/reports"),

        new("sales-summary",
            "/api/reports/sales-summary",
            "Plain sales totals for a date range, optionally for one location.",
            "from, to, locationId",
            "/reports/sales-summary"),

        new("customer-aging",
            "/api/reports/aging/customer",
            "Receivables split into age buckets.",
            "asOf (yyyy-MM-dd)",
            "/reports/aging/customer"),

        new("top-customers",
            "/api/reports/top-customers",
            "Who buys the most.",
            "from, to, take",
            "/reports/top-customers"),
    };

    /// <summary>The menu, as text for the model to choose from.</summary>
    public static string AsPrompt() =>
        string.Join("\n", All.Select(e =>
            $"- key: {e.Key}\n  answers: {e.Answers}\n  parameters: {e.Parameters}"));

    public static Entry? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(e =>
            string.Equals(e.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
