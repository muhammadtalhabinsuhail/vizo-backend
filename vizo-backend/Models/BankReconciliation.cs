using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class BankReconciliation
{
    public int ReconciliationId { get; set; }

    public int AccountId { get; set; }

    public DateOnly StatementDate { get; set; }

    public decimal OpeningBalance { get; set; }

    public decimal ClosingBalance { get; set; }

    public int StatusId { get; set; }

    public int PreparedByUserId { get; set; }

    public DateOnly? FinalizedOn { get; set; }

    public virtual Account Account { get; set; } = null!;

    public virtual ICollection<BankStatementLine> BankStatementLines { get; set; } = new List<BankStatementLine>();

    public virtual Employee PreparedByUser { get; set; } = null!;

    public virtual PostingStatus Status { get; set; } = null!;
}
