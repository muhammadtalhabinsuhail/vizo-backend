using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class JournalEntryLine
{
    public int LineId { get; set; }

    public int EntryId { get; set; }

    public short LineNo { get; set; }

    public int AccountId { get; set; }

    public int? PartyUserId { get; set; }

    public string? Description { get; set; }

    public decimal DebitAmount { get; set; }

    public decimal CreditAmount { get; set; }

    public virtual Account Account { get; set; } = null!;

    public virtual ICollection<BankStatementLine> BankStatementLines { get; set; } = new List<BankStatementLine>();

    public virtual JournalEntry Entry { get; set; } = null!;

    public virtual Party? PartyUser { get; set; }
}
