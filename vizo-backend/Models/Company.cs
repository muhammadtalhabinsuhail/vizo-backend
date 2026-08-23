using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Company
{
    public int CompanyId { get; set; }

    public string CompanyName { get; set; } = null!;

    public string LegalName { get; set; } = null!;

    public string AddressLine { get; set; } = null!;

    public int CityId { get; set; }

    public string Country { get; set; } = null!;

    public string Phone { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string Ntn { get; set; } = null!;

    public string Strn { get; set; } = null!;

    public short FiscalYearStartMonth { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public string CurrencySymbol { get; set; } = null!;

    public decimal ForeignRate { get; set; }

    public virtual City City { get; set; } = null!;
}
