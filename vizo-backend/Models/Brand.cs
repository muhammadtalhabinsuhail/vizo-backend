using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Brand
{
    public int BrandId { get; set; }

    public string BrandCode { get; set; } = null!;

    public string BrandName { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
