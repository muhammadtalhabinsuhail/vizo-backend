using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class BankStatementLine
{
    public int StatementLineId { get; set; }

    public int ReconciliationId { get; set; }

    public DateOnly LineDate { get; set; }

    public string Description { get; set; } = null!;

    public decimal Amount { get; set; }

    public int? MatchedLineId { get; set; }

    public virtual JournalEntryLine? MatchedLine { get; set; }

    public virtual BankReconciliation Reconciliation { get; set; } = null!;
}
