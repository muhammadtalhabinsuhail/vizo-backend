using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Province
{
    public int ProvinceId { get; set; }

    public string ProvinceName { get; set; } = null!;

    public virtual ICollection<City> Cities { get; set; } = new List<City>();
}
