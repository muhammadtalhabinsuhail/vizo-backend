using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class DocumentSeries
{
    public int SeriesId { get; set; }

    public string SeriesKey { get; set; } = null!;

    public string Label { get; set; } = null!;

    public string Prefix { get; set; } = null!;

    public bool IncludeYear { get; set; }

    public short Padding { get; set; }

    public int NextNumber { get; set; }
}
