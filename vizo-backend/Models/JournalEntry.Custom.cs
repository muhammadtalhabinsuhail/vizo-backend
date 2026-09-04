namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// JournalEntry.cs is scaffolded from Neon and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column this project added lives here
/// instead. Mapped in AppDbContext.Custom.cs; created on Neon by
/// backend/database/12_journal_reversal_link.sql.
///
/// AFTER A RE-SCAFFOLD: the column now exists on Neon, so a fresh scaffold will
/// generate ReversedByEntryId in JournalEntry.cs itself. Delete this file then,
/// or you will get a duplicate-definition error.
/// </summary>
public partial class JournalEntry
{
    /// <summary>
    /// The entry that undid this one, or null if it still stands.
    ///
    /// The original keeps its POSTED status on purpose. Every statement filters
    /// on POSTED, so un-posting the original would have left the mirror
    /// standing alone and the ledger holding the negative of the original.
    /// Both entries count and cancel each other; this column is what lets the
    /// screen say which one cancelled which.
    /// </summary>
    public int? ReversedByEntryId { get; set; }

    /// <summary>The mirror entry, when one has been written.</summary>
    public virtual JournalEntry? ReversedByEntry { get; set; }

    /// <summary>The entry this one was written to undo, if it is a mirror.</summary>
    public virtual ICollection<JournalEntry> Reverses { get; set; } = new List<JournalEntry>();
}
