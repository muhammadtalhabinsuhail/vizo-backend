using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class City
{
    public int CityId { get; set; }

    public string CityName { get; set; } = null!;

    public int ProvinceId { get; set; }

    public virtual ICollection<Company> Companies { get; set; } = new List<Company>();

    public virtual ICollection<Location> Locations { get; set; } = new List<Location>();

    public virtual ICollection<Party> Parties { get; set; } = new List<Party>();

    public virtual Province Province { get; set; } = null!;
}
