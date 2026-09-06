using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Province
{
    public int ProvinceId { get; set; }

    public string ProvinceName { get; set; } = null!;

    /// <summary>
    /// ISO 3166-1 alpha-2, "PK" or "CN". Added in 17_party_country.sql.
    ///
    /// A party's country is not stored on the party -- it is read from here,
    /// through the city, because the province already knows and two answers to
    /// one question is one answer too many.
    /// </summary>
    public string Country { get; set; } = "PK";

    public virtual ICollection<City> Cities { get; set; } = new List<City>();
}
