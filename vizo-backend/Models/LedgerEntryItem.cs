using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// The item, quantity and price written on a hand-made customer ledger row.
///
/// HAND-WRITTEN, not scaffolded. Created by backend/database/32_ledger_entry_items.sql.
/// The row itself is only a journal entry -- no invoice, no stock movement --
/// so the goods it describes cannot live on an invoice line; they hang off the
/// entry here, and the statement prints them indented under it the way it
/// prints a sale's items.
/// </summary>
[Table("LedgerEntryItem")]
public class LedgerEntryItem
{
    [Key]
    public int LedgerEntryItemId { get; set; }

    public int EntryId { get; set; }

    public short LineNo { get; set; } = 1;

    /// <summary>Null when the item was typed as free text rather than picked.</summary>
    public int? ProductId { get; set; }

    /// <summary>What it was called when it was written -- kept if the product is renamed.</summary>
    [MaxLength(200)]
    public string ItemName { get; set; } = null!;

    [Precision(12, 2)]
    public decimal Qty { get; set; }

    [Precision(14, 2)]
    public decimal Rate { get; set; }

    [Precision(14, 2)]
    public decimal Amount { get; set; }
}
