using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class JournalEntryType
{
    public int EntryTypeId { get; set; }

    public string TypeKey { get; set; } = null!;

    public string TypeName { get; set; } = null!;

    public virtual ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();
}
