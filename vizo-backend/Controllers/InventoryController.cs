using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// The /inventory screens: products, categories, brands, stock levels,
/// movements, adjustments and transfers.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports through
/// Fail().
///
/// Stock is never stored on the product. "StockBalance" holds quantity per
/// (product, location) and is the only truth; a product's total is the sum
/// across locations, computed in the query.
/// </summary>
[Route("api/inventory")]
[ApiController]
[Authorize(Policy = "BackOffice")]
public class InventoryController : ApiControllerBase
{
    public InventoryController(AppDbContext db, IConfiguration cfg,
        ILogger<InventoryController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    // ══════════════════════════════════════════════════════════════════
    //  PRODUCTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? q, [FromQuery] int? categoryId, [FromQuery] int? brandId,
        [FromQuery] string? status, [FromQuery] bool includeInactive = true,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.Products.AsNoTracking().AsQueryable();

            if (categoryId is not null) rows = rows.Where(p => p.CategoryId == categoryId);
            if (brandId is not null) rows = rows.Where(p => p.BrandId == brandId);
            if (!includeInactive) rows = rows.Where(p => p.IsActive);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p => p.ProductName.ToLower().Contains(term) ||
                                       p.Sku.ToLower().Contains(term) ||
                                       p.ProductBarcodes.Any(b => b.Barcode.Contains(term)));
            }

            var total = await rows.CountAsync();

            var items = await rows
                .OrderBy(p => p.ProductName)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    description = p.Description,
                    categoryId = p.CategoryId,
                    categoryName = p.Category.CategoryName,
                    brandId = p.BrandId,
                    brandName = p.Brand.BrandName,
                    packing = p.Packing,
                    minQty = p.MinQty,
                    maxQty = p.MaxQty,
                    openingCost = p.OpeningCost,
                    costPrice = p.CostPrice,
                    salePrice = p.SalePrice,
                    taxRatePercent = p.TaxRatePercent,
                    hideStock = p.HideStock,
                    isActive = p.IsActive,
                    imageUrl = p.ImageUrl,
                    createdAt = p.CreatedAt,
                    totalStock = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    barcodes = p.ProductBarcodes.Select(b => b.Barcode).ToList()
                })
                .ToListAsync();

            /* status is derived, not stored: out -> low -> inactive -> active. */
            var shaped = items.Select(p => new
            {
                p.id, p.sku, p.name, p.description,
                p.categoryId, p.categoryName, p.brandId, p.brandName,
                p.packing, p.minQty, p.maxQty,
                p.openingCost, p.costPrice, p.salePrice, p.taxRatePercent,
                p.hideStock, p.isActive, p.imageUrl, p.createdAt,
                p.totalStock, p.barcodes,
                status = !p.isActive ? "inactive"
                       : p.totalStock <= 0 ? "out"
                       : p.totalStock <= p.minQty ? "low" : "active"
            }).ToList();

            if (!string.IsNullOrWhiteSpace(status))
                shaped = shaped.Where(p => p.status == status).ToList();

            return Ok(new { total, page, pageSize, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the product list");
        }
    }

    [HttpGet("products/{id:int}")]
    public async Task<IActionResult> GetProduct(int id)
    {
        try
        {
            var p = await _db.Products.AsNoTracking()
                .Where(x => x.ProductId == id)
                .Select(x => new
                {
                    id = x.ProductId,
                    sku = x.Sku,
                    name = x.ProductName,
                    description = x.Description,
                    categoryId = x.CategoryId,
                    categoryName = x.Category.CategoryName,
                    brandId = x.BrandId,
                    brandName = x.Brand.BrandName,
                    packing = x.Packing,
                    minQty = x.MinQty,
                    maxQty = x.MaxQty,
                    openingCost = x.OpeningCost,
                    costPrice = x.CostPrice,
                    salePrice = x.SalePrice,
                    taxRatePercent = x.TaxRatePercent,
                    hideStock = x.HideStock,
                    isActive = x.IsActive,
                    imageUrl = x.ImageUrl,
                    createdAt = x.CreatedAt,
                    barcodes = x.ProductBarcodes.Select(b => b.Barcode).ToList(),
                    totalStock = x.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    stockSpread = x.StockBalances.Select(s => new
                    {
                        locationId = s.LocationId,
                        locationCode = s.Location.LocationCode,
                        locationName = s.Location.LocationName,
                        qty = s.Quantity
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No product with id {id}." });

            return Ok(new
            {
                p.id, p.sku, p.name, p.description,
                p.categoryId, p.categoryName, p.brandId, p.brandName,
                p.packing, p.minQty, p.maxQty,
                p.openingCost, p.costPrice, p.salePrice, p.taxRatePercent,
                p.hideStock, p.isActive, p.imageUrl, p.createdAt,
                p.barcodes, p.totalStock, p.stockSpread,
                status = !p.isActive ? "inactive"
                       : p.totalStock <= 0 ? "out"
                       : p.totalStock <= p.minQty ? "low" : "active"
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load product {id}");
        }
    }

    [HttpPost("products")]
    public async Task<IActionResult> CreateProduct([FromBody] ProductRequest body)
    {
        try
        {
            var error = await ValidateProduct(body, null);
            if (error is not null) return BadRequest(new { message = error });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var product = new Product
            {
                Sku = body.Sku.Trim().ToUpperInvariant(),
                ProductName = body.Name.Trim(),
                Description = body.Description,
                CategoryId = body.CategoryId,
                BrandId = body.BrandId,
                Packing = body.Packing,
                MinQty = body.MinQty,
                MaxQty = body.MaxQty,
                OpeningCost = body.OpeningCost,
                CostPrice = body.CostPrice,
                SalePrice = body.SalePrice,
                TaxRatePercent = body.TaxRatePercent,
                HideStock = body.HideStock,
                IsActive = body.IsActive,
                ImageUrl = body.ImageUrl,
                CreatedAt = Today()
            };
            _db.Products.Add(product);
            await _db.SaveChangesAsync();

            foreach (var code in (body.Barcodes ?? new List<string>())
                     .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
            {
                _db.ProductBarcodes.Add(new ProductBarcode
                {
                    ProductId = product.ProductId,
                    Barcode = code.Trim()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PRODUCT_CREATED", "Product", product.Sku, product.ProductName, 1);
            return Ok(new { id = product.ProductId, message = $"{product.ProductName} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the product");
        }
    }

    [HttpPut("products/{id:int}")]
    public async Task<IActionResult> UpdateProduct(int id, [FromBody] ProductRequest body)
    {
        try
        {
            var product = await _db.Products
                .Include(p => p.ProductBarcodes)
                .FirstOrDefaultAsync(p => p.ProductId == id);
            if (product is null) return NotFound(new { message = $"No product with id {id}." });

            var error = await ValidateProduct(body, id);
            if (error is not null) return BadRequest(new { message = error });

            product.Sku = body.Sku.Trim().ToUpperInvariant();
            product.ProductName = body.Name.Trim();
            product.Description = body.Description;
            product.CategoryId = body.CategoryId;
            product.BrandId = body.BrandId;
            product.Packing = body.Packing;
            product.MinQty = body.MinQty;
            product.MaxQty = body.MaxQty;
            product.OpeningCost = body.OpeningCost;
            product.CostPrice = body.CostPrice;
            product.SalePrice = body.SalePrice;
            product.TaxRatePercent = body.TaxRatePercent;
            product.HideStock = body.HideStock;
            product.IsActive = body.IsActive;
            product.ImageUrl = body.ImageUrl;

            var wanted = (body.Barcodes ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct().ToList();

            _db.ProductBarcodes.RemoveRange(
                product.ProductBarcodes.Where(b => !wanted.Contains(b.Barcode)));
            foreach (var code in wanted.Where(c => product.ProductBarcodes.All(b => b.Barcode != c)))
                _db.ProductBarcodes.Add(new ProductBarcode { ProductId = id, Barcode = code });

            await _db.SaveChangesAsync();
            await Log("PRODUCT_UPDATED", "Product", product.Sku, product.ProductName, 1);

            return Ok(new { id, message = $"{product.ProductName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save product {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CATEGORIES AND BRANDS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        try
        {
            return Ok(await _db.Categories.AsNoTracking()
                .OrderBy(c => c.CategoryName)
                .Select(c => new
                {
                    id = c.CategoryId,
                    name = c.CategoryName,
                    parentId = c.ParentCategoryId,
                    parentName = c.ParentCategory != null ? c.ParentCategory.CategoryName : null,
                    isActive = c.IsActive,
                    productCount = c.Products.Count
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load categories");
        }
    }

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory([FromBody] CategoryRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Category name is required." });

            var name = body.Name.Trim();
            if (await _db.Categories.AnyAsync(c => c.CategoryName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"A category called {name} already exists." });

            var c = new Category
            {
                CategoryName = name,
                ParentCategoryId = body.ParentId,
                IsActive = body.IsActive
            };
            _db.Categories.Add(c);
            await _db.SaveChangesAsync();
            await Log("CATEGORY_CREATED", "Category", c.CategoryId.ToString(), name, 1);

            return Ok(new { id = c.CategoryId, message = $"{name} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the category");
        }
    }

    [HttpPut("categories/{id:int}")]
    public async Task<IActionResult> UpdateCategory(int id, [FromBody] CategoryRequest body)
    {
        try
        {
            var c = await _db.Categories.FirstOrDefaultAsync(x => x.CategoryId == id);
            if (c is null) return NotFound(new { message = $"No category with id {id}." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Category name is required." });
            if (body.ParentId == id)
                return BadRequest(new { message = "A category cannot be its own parent." });

            c.CategoryName = body.Name.Trim();
            c.ParentCategoryId = body.ParentId;
            c.IsActive = body.IsActive;
            await _db.SaveChangesAsync();
            await Log("CATEGORY_UPDATED", "Category", id.ToString(), c.CategoryName, 1);

            return Ok(new { id, message = $"{c.CategoryName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save category {id}");
        }
    }

    [HttpGet("brands")]
    public async Task<IActionResult> GetBrands()
    {
        try
        {
            return Ok(await _db.Brands.AsNoTracking()
                .OrderBy(b => b.BrandName)
                .Select(b => new
                {
                    id = b.BrandId,
                    code = b.BrandCode,
                    name = b.BrandName,
                    description = b.Description,
                    isActive = b.IsActive,
                    productCount = b.Products.Count
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load brands");
        }
    }

    [HttpPost("brands")]
    public async Task<IActionResult> CreateBrand([FromBody] BrandRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Brand name is required." });
            if (string.IsNullOrWhiteSpace(body.Code))
                return BadRequest(new { message = "Brand code is required." });

            var code = body.Code.Trim().ToUpperInvariant();
            if (await _db.Brands.AnyAsync(b => b.BrandCode.ToUpper() == code))
                return BadRequest(new { message = $"Brand code {code} is already in use." });

            var b = new Brand
            {
                BrandCode = code,
                BrandName = body.Name.Trim(),
                Description = body.Description,
                IsActive = body.IsActive
            };
            _db.Brands.Add(b);
            await _db.SaveChangesAsync();
            await Log("BRAND_CREATED", "Brand", code, b.BrandName, 1);

            return Ok(new { id = b.BrandId, message = $"{b.BrandName} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "create the brand");
        }
    }

    [HttpPut("brands/{id:int}")]
    public async Task<IActionResult> UpdateBrand(int id, [FromBody] BrandRequest body)
    {
        try
        {
            var b = await _db.Brands.FirstOrDefaultAsync(x => x.BrandId == id);
            if (b is null) return NotFound(new { message = $"No brand with id {id}." });
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest(new { message = "Brand name is required." });

            var code = (body.Code ?? b.BrandCode).Trim().ToUpperInvariant();
            if (await _db.Brands.AnyAsync(x => x.BrandCode.ToUpper() == code && x.BrandId != id))
                return BadRequest(new { message = $"Brand code {code} is already in use." });

            b.BrandCode = code;
            b.BrandName = body.Name.Trim();
            b.Description = body.Description;
            b.IsActive = body.IsActive;
            await _db.SaveChangesAsync();
            await Log("BRAND_UPDATED", "Brand", code, b.BrandName, 1);

            return Ok(new { id, message = $"{b.BrandName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save brand {id}");
        }
    }


    /// <summary>
    /// Deletes a category. Refuses while products still point at it -- the FK
    /// would reject it anyway, but a clear message beats a 23503 in the log.
    /// </summary>
    [HttpDelete("categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        try
        {
            var c = await _db.Categories
                .Include(x => x.Products)
                .Include(x => x.InverseParentCategory)
                .FirstOrDefaultAsync(x => x.CategoryId == id);
            if (c is null) return NotFound(new { message = $"No category with id {id}." });

            if (c.Products.Count > 0)
                return BadRequest(new
                {
                    message = $"{c.CategoryName} still has {c.Products.Count} product(s). " +
                              "Move them to another category first, or set this one inactive instead."
                });
            if (c.InverseParentCategory.Count > 0)
                return BadRequest(new
                {
                    message = $"{c.CategoryName} has {c.InverseParentCategory.Count} sub-categor" +
                              (c.InverseParentCategory.Count == 1 ? "y" : "ies") + ". Remove those first."
                });

            var name = c.CategoryName;
            _db.Categories.Remove(c);
            await _db.SaveChangesAsync();
            await Log("CATEGORY_DELETED", "Category", id.ToString(), name, 3);

            return Ok(new { id, message = $"{name} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete category {id}");
        }
    }

    /// <summary>Deletes a brand. Refuses while products still point at it.</summary>
    [HttpDelete("brands/{id:int}")]
    public async Task<IActionResult> DeleteBrand(int id)
    {
        try
        {
            var b = await _db.Brands.Include(x => x.Products)
                .FirstOrDefaultAsync(x => x.BrandId == id);
            if (b is null) return NotFound(new { message = $"No brand with id {id}." });

            if (b.Products.Count > 0)
                return BadRequest(new
                {
                    message = $"{b.BrandName} still has {b.Products.Count} product(s). " +
                              "Reassign them first, or set this brand inactive instead."
                });

            var name = b.BrandName;
            _db.Brands.Remove(b);
            await _db.SaveChangesAsync();
            await Log("BRAND_DELETED", "Brand", id.ToString(), name, 3);

            return Ok(new { id, message = $"{name} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete brand {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STOCK LEVELS AND MOVEMENTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("stock-levels")]
    public async Task<IActionResult> GetStockLevels(
        [FromQuery] int? locationId, [FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.StockBalances.AsNoTracking().AsQueryable();

            if (locationId is not null) rows = rows.Where(s => s.LocationId == locationId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(s => s.Product.ProductName.ToLower().Contains(term) ||
                                       s.Product.Sku.ToLower().Contains(term));
            }

            var items = await rows
                .OrderBy(s => s.Product.ProductName).ThenBy(s => s.Location.LocationName)
                .Select(s => new
                {
                    productId = s.ProductId,
                    sku = s.Product.Sku,
                    name = s.Product.ProductName,
                    packing = s.Product.Packing,
                    minQty = s.Product.MinQty,
                    maxQty = s.Product.MaxQty,
                    costPrice = s.Product.CostPrice,
                    locationId = s.LocationId,
                    locationCode = s.Location.LocationCode,
                    locationName = s.Location.LocationName,
                    qty = s.Quantity
                })
                .ToListAsync();

            var shaped = items.Select(s => new
            {
                s.productId, s.sku, s.name, s.packing, s.minQty, s.maxQty, s.costPrice,
                s.locationId, s.locationCode, s.locationName, s.qty,
                packets = s.packing > 0 ? s.qty / s.packing : 0,
                loose = s.packing > 0 ? s.qty % s.packing : s.qty,
                value = s.qty * s.costPrice,
                status = s.qty <= 0 ? "out"
                       : s.qty <= s.minQty ? "low"
                       : s.maxQty > 0 && s.qty > s.maxQty ? "over" : "ok"
            }).ToList();

            if (!string.IsNullOrWhiteSpace(status))
                shaped = shaped.Where(s => s.status == status).ToList();

            return Ok(new
            {
                totalValue = shaped.Sum(s => s.value),
                totalUnits = shaped.Sum(s => s.qty),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock levels");
        }
    }

    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements(
        [FromQuery] int? productId, [FromQuery] int? locationId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.StockMovements.AsNoTracking().AsQueryable();

            if (productId is not null) rows = rows.Where(m => m.ProductId == productId);
            if (locationId is not null) rows = rows.Where(m => m.LocationId == locationId);
            if (from is not null)
                rows = rows.Where(m => m.MovedAt >= from.Value.ToDateTime(TimeOnly.MinValue));
            if (to is not null)
                rows = rows.Where(m => m.MovedAt <= to.Value.ToDateTime(TimeOnly.MaxValue));

            var total = await rows.CountAsync();

            var items = await rows
                .OrderByDescending(m => m.MovedAt).ThenByDescending(m => m.MovementId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(m => new
                {
                    id = m.MovementId,
                    productId = m.ProductId,
                    sku = m.Product.Sku,
                    name = m.Product.ProductName,
                    locationId = m.LocationId,
                    locationName = m.Location.LocationName,
                    movementType = m.MovementType.TypeKey,
                    movementTypeName = m.MovementType.TypeName,
                    movedAt = m.MovedAt,
                    referenceNo = m.ReferenceNo,
                    qty = m.Quantity,
                    balanceAfter = m.BalanceAfter,
                    user = m.User.FullName
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock movements");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ADJUSTMENTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("adjustments")]
    public async Task<IActionResult> GetAdjustments([FromQuery] string? status)
    {
        try
        {
            var rows = _db.StockAdjustments.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(a => a.Status.StatusKey == status);

            return Ok(await rows
                .OrderByDescending(a => a.AdjustmentDate).ThenByDescending(a => a.AdjustmentId)
                .Select(a => new
                {
                    id = a.AdjustmentId,
                    adjustmentNo = a.AdjustmentNo,
                    locationId = a.LocationId,
                    locationName = a.Location.LocationName,
                    adjustmentDate = a.AdjustmentDate,
                    reason = a.Reason.ReasonKey,
                    reasonName = a.Reason.ReasonName,
                    reasonNotes = a.ReasonNotes,
                    status = a.Status.StatusKey,
                    statusName = a.Status.StatusName,
                    createdBy = a.CreatedByUser.User.FullName,
                    itemCount = a.StockAdjustmentItems.Count,
                    netUnits = a.StockAdjustmentItems.Sum(i => (int?)(i.NewQty - i.CurrentQty)) ?? 0
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock adjustments");
        }
    }

    [HttpGet("adjustments/{id:int}")]
    public async Task<IActionResult> GetAdjustment(int id)
    {
        try
        {
            var a = await _db.StockAdjustments.AsNoTracking()
                .Where(x => x.AdjustmentId == id)
                .Select(x => new
                {
                    id = x.AdjustmentId,
                    adjustmentNo = x.AdjustmentNo,
                    locationId = x.LocationId,
                    locationName = x.Location.LocationName,
                    adjustmentDate = x.AdjustmentDate,
                    reason = x.Reason.ReasonKey,
                    reasonName = x.Reason.ReasonName,
                    reasonNotes = x.ReasonNotes,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    createdBy = x.CreatedByUser.User.FullName,
                    lines = x.StockAdjustmentItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.AdjustmentItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        name = i.Product.ProductName,
                        currentQty = i.CurrentQty,
                        newQty = i.NewQty,
                        delta = i.NewQty - i.CurrentQty,
                        costPrice = i.Product.CostPrice
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (a is null) return NotFound(new { message = $"No adjustment with id {id}." });
            return Ok(a);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load adjustment {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  TRANSFERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("transfers")]
    public async Task<IActionResult> GetTransfers([FromQuery] string? status)
    {
        try
        {
            var rows = _db.StockTransfers.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(t => t.Status.StatusKey == status);

            return Ok(await rows
                .OrderByDescending(t => t.TransferDate).ThenByDescending(t => t.TransferId)
                .Select(t => new
                {
                    id = t.TransferId,
                    transferNo = t.TransferNo,
                    fromLocationId = t.FromLocationId,
                    fromLocation = t.FromLocation.LocationName,
                    toLocationId = t.ToLocationId,
                    toLocation = t.ToLocation.LocationName,
                    transferDate = t.TransferDate,
                    receivedOn = t.ReceivedOn,
                    status = t.Status.StatusKey,
                    statusName = t.Status.StatusName,
                    initiatedBy = t.InitiatedByUser.User.FullName,
                    approvedBy = t.ApprovedByUser != null ? t.ApprovedByUser.User.FullName : null,
                    notes = t.Notes,
                    itemCount = t.StockTransferItems.Count,
                    totalUnits = t.StockTransferItems.Sum(i => (int?)i.Quantity) ?? 0
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load stock transfers");
        }
    }

    [HttpGet("transfers/{id:int}")]
    public async Task<IActionResult> GetTransfer(int id)
    {
        try
        {
            var t = await _db.StockTransfers.AsNoTracking()
                .Where(x => x.TransferId == id)
                .Select(x => new
                {
                    id = x.TransferId,
                    transferNo = x.TransferNo,
                    fromLocationId = x.FromLocationId,
                    fromLocation = x.FromLocation.LocationName,
                    toLocationId = x.ToLocationId,
                    toLocation = x.ToLocation.LocationName,
                    transferDate = x.TransferDate,
                    receivedOn = x.ReceivedOn,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    initiatedBy = x.InitiatedByUser.User.FullName,
                    approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                    notes = x.Notes,
                    lines = x.StockTransferItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.TransferItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        name = i.Product.ProductName,
                        qty = i.Quantity,
                        packing = i.Product.Packing
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (t is null) return NotFound(new { message = $"No transfer with id {id}." });
            return Ok(t);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load transfer {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                categories = await _db.Categories.AsNoTracking()
                    .Where(c => c.IsActive).OrderBy(c => c.CategoryName)
                    .Select(c => new { id = c.CategoryId, name = c.CategoryName, parentId = c.ParentCategoryId })
                    .ToListAsync(),
                brands = await _db.Brands.AsNoTracking()
                    .Where(b => b.IsActive).OrderBy(b => b.BrandName)
                    .Select(b => new { id = b.BrandId, code = b.BrandCode, name = b.BrandName })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                adjustmentReasons = await _db.AdjustmentReasons.AsNoTracking()
                    .Select(r => new { id = r.ReasonId, key = r.ReasonKey, name = r.ReasonName })
                    .ToListAsync(),
                movementTypes = await _db.MovementTypes.AsNoTracking()
                    .Select(m => new { id = m.MovementTypeId, key = m.TypeKey, name = m.TypeName })
                    .ToListAsync(),
                transferStatuses = await _db.TransferStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),

                /* Every active product, for the item pickers on the adjustment
                   and transfer forms. Those two screens used to import a
                   hard-coded array from the frontend's src/data/products, so a
                   product created minutes earlier could not be adjusted or
                   transferred at all -- it simply was not in the list. Read
                   live here so the picker is never behind the catalogue.
                   totalStock is the sum across locations; the per-location
                   figure the adjustment form actually needs comes from
                   GET /inventory/stock-levels?locationId=. */
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.ProductName)
                    .Select(p => new
                    {
                        id = p.ProductId,
                        sku = p.Sku,
                        name = p.ProductName,
                        packing = p.Packing,
                        costPrice = p.CostPrice,
                        salePrice = p.SalePrice,
                        totalStock = p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load inventory lookups");
        }
    }

    // ════════════════════════ validation helpers ════════════════════════

    private async Task<string?> ValidateProduct(ProductRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.Name)) return "Product name is required.";
        if (string.IsNullOrWhiteSpace(b.Sku)) return "SKU is required.";
        if (b.Packing < 1) return "Packing must be at least 1.";
        if (b.MinQty < 0) return "Minimum quantity cannot be negative.";
        if (b.MaxQty < 0) return "Maximum quantity cannot be negative.";
        if (b.MaxQty > 0 && b.MaxQty < b.MinQty)
            return "Maximum quantity cannot be below the minimum.";
        if (b.CostPrice < 0 || b.SalePrice < 0) return "Prices cannot be negative.";
        if (b.TaxRatePercent is < 0 or > 100) return "Tax rate must be between 0 and 100.";

        var sku = b.Sku.Trim().ToUpperInvariant();
        if (await _db.Products.AnyAsync(p => p.Sku.ToUpper() == sku &&
                                             (existingId == null || p.ProductId != existingId)))
            return $"SKU {sku} is already in use.";

        if (!await _db.Categories.AnyAsync(c => c.CategoryId == b.CategoryId))
            return "Pick a valid category.";
        if (!await _db.Brands.AnyAsync(x => x.BrandId == b.BrandId))
            return "Pick a valid brand.";

        return null;
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record ProductRequest(
        string Sku, string Name, string? Description, int CategoryId, int BrandId,
        int Packing, int MinQty, int MaxQty,
        decimal OpeningCost, decimal CostPrice, decimal SalePrice, decimal TaxRatePercent,
        bool HideStock, bool IsActive, string? ImageUrl, List<string>? Barcodes);

    public record CategoryRequest(string Name, int? ParentId, bool IsActive);

    public record BrandRequest(string Code, string Name, string? Description, bool IsActive);

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  adjustments and transfers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Corrects the shelf count. The line carries CurrentQty (what the system
    /// thought) and NewQty (what was actually counted); the difference is what
    /// moves. CurrentQty is re-read from StockBalance here rather than trusted
    /// from the browser, because between opening the form and saving it somebody
    /// else may have sold the same item -- writing the client's stale figure back
    /// would silently undo their sale.
    /// </summary>
    [HttpPost("adjustments")]
    public async Task<IActionResult> CreateAdjustment([FromBody] AdjustmentRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "An adjustment needs at least one line." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (!await _db.AdjustmentReasons.AnyAsync(r => r.ReasonId == body.ReasonId))
                return BadRequest(new { message = "Pick a valid reason." });
            foreach (var l in body.Lines)
            {
                if (l.NewQty < 0) return BadRequest(new { message = "A counted quantity cannot be negative." });
                if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                    return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
            }

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can correct stock." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "POSTED");
            var type = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "ADJUSTMENT");
            if (posted is null || type is null)
                return BadRequest(new { message = "POSTED status or ADJUSTMENT movement type is not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var adj = new StockAdjustment
            {
                AdjustmentNo = await NextNumber("ADJ"),
                LocationId = body.LocationId,
                AdjustmentDate = body.AdjustmentDate ?? Today(),
                ReasonId = body.ReasonId,
                ReasonNotes = body.ReasonNotes ?? "",
                StatusId = posted.StatusId,
                CreatedByUserId = me.Value
            };
            _db.StockAdjustments.Add(adj);
            await _db.SaveChangesAsync();

            short n = 1;
            var moved = 0;
            foreach (var l in body.Lines)
            {
                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = body.LocationId, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                    await _db.SaveChangesAsync();
                }

                var current = bal.Quantity;          // the truth, right now
                var delta = l.NewQty - current;

                _db.StockAdjustmentItems.Add(new StockAdjustmentItem
                {
                    AdjustmentId = adj.AdjustmentId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    CurrentQty = current,
                    NewQty = l.NewQty
                });

                if (delta == 0) continue;          // counted the same, nothing to move
                bal.Quantity = l.NewQty;
                moved++;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.LocationId,
                    MovementTypeId = type.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = adj.AdjustmentNo,
                    Quantity = delta,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("STOCK_ADJUSTED", "StockAdjustment", adj.AdjustmentNo,
                $"{moved} of {body.Lines.Count} lines moved", 3);

            return Ok(new
            {
                id = adj.AdjustmentId,
                adjustmentNo = adj.AdjustmentNo,
                linesChanged = moved,
                message = moved == 0
                    ? $"{adj.AdjustmentNo} saved. Every line matched the system count, so no stock moved."
                    : $"{adj.AdjustmentNo} posted. {moved} line(s) corrected."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "post the stock adjustment");
        }
    }

    /// <summary>
    /// Moves stock between two locations. Goods leave the FROM shelf
    /// immediately and are only added to the TO shelf when the receiving end
    /// confirms (POST transfers/{id}/receive) -- stock in a van belongs to
    /// neither shelf, and counting it in both is how a transfer creates
    /// inventory out of nothing.
    /// </summary>
    [HttpPost("transfers")]
    public async Task<IActionResult> CreateTransfer([FromBody] TransferRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A transfer needs at least one line." });
            if (body.FromLocationId == body.ToLocationId)
                return BadRequest(new { message = "From and To must be different locations." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.FromLocationId))
                return BadRequest(new { message = "Pick a valid source location." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.ToLocationId))
                return BadRequest(new { message = "Pick a valid destination location." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can move stock." });

            var status = await _db.TransferStatuses.FirstOrDefaultAsync(s => s.StatusKey == "IN_TRANSIT");
            var outType = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "TRANSFER_OUT");
            if (status is null || outType is null)
                return BadRequest(new { message = "IN_TRANSIT status or TRANSFER_OUT movement type is not configured." });

            /* Check every line before moving any of them, so a short line on
               row 5 does not leave rows 1-4 already deducted. */
            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });
                var have = await _db.StockBalances
                    .Where(s => s.ProductId == l.ProductId && s.LocationId == body.FromLocationId)
                    .Select(s => (int?)s.Quantity).FirstOrDefaultAsync() ?? 0;
                if (have < l.Qty)
                {
                    var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == l.ProductId);
                    return BadRequest(new
                    {
                        message = $"{p?.ProductName ?? $"Product {l.ProductId}"}: asked for {l.Qty}, only {have} on the source shelf."
                    });
                }
            }

            await using var tx = await _db.Database.BeginTransactionAsync();

            var tr = new StockTransfer
            {
                TransferNo = await NextNumber("TRF"),
                FromLocationId = body.FromLocationId,
                ToLocationId = body.ToLocationId,
                TransferDate = body.TransferDate ?? Today(),
                StatusId = status.StatusId,
                InitiatedByUserId = me.Value,
                ApprovedByUserId = null,
                ReceivedOn = null,
                Notes = body.Notes
            };
            _db.StockTransfers.Add(tr);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                _db.StockTransferItems.Add(new StockTransferItem
                {
                    TransferId = tr.TransferId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty
                });

                var from = await _db.StockBalances
                    .FirstAsync(s => s.ProductId == l.ProductId && s.LocationId == body.FromLocationId);
                from.Quantity -= l.Qty;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.FromLocationId,
                    MovementTypeId = outType.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = tr.TransferNo,
                    Quantity = -l.Qty,
                    BalanceAfter = from.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("TRANSFER_SENT", "StockTransfer", tr.TransferNo,
                $"{body.Lines.Count} lines", 2);

            return Ok(new
            {
                id = tr.TransferId,
                transferNo = tr.TransferNo,
                message = $"{tr.TransferNo} sent. Stock leaves the source now and lands when the destination receives it."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "send the stock transfer");
        }
    }

    /// <summary>The receiving end confirms; this is when stock lands on the TO shelf.</summary>
    [HttpPost("transfers/{id:int}/receive")]
    public async Task<IActionResult> ReceiveTransfer(int id)
    {
        try
        {
            var tr = await _db.StockTransfers
                .Include(t => t.Status)
                .Include(t => t.StockTransferItems)
                .FirstOrDefaultAsync(t => t.TransferId == id);

            if (tr is null) return NotFound(new { message = $"No transfer with id {id}." });
            if (tr.Status.StatusKey == "RECEIVED")
                return BadRequest(new { message = $"{tr.TransferNo} was already received." });
            if (tr.Status.StatusKey is "DRAFT" or "REJECTED")
                return BadRequest(new { message = $"{tr.TransferNo} is {tr.Status.StatusName} and has not been sent." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can receive a transfer." });

            var received = await _db.TransferStatuses.FirstOrDefaultAsync(s => s.StatusKey == "RECEIVED");
            var inType = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "TRANSFER_IN");
            if (received is null || inType is null)
                return BadRequest(new { message = "RECEIVED status or TRANSFER_IN movement type is not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            foreach (var l in tr.StockTransferItems)
            {
                var to = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == tr.ToLocationId);
                if (to is null)
                {
                    to = new StockBalance { ProductId = l.ProductId, LocationId = tr.ToLocationId, Quantity = 0 };
                    _db.StockBalances.Add(to);
                    await _db.SaveChangesAsync();
                }
                to.Quantity += l.Quantity;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = tr.ToLocationId,
                    MovementTypeId = inType.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = tr.TransferNo,
                    Quantity = l.Quantity,
                    BalanceAfter = to.Quantity,
                    UserId = CurrentUserId()
                });
            }

            tr.StatusId = received.StatusId;
            tr.ReceivedOn = Today();
            tr.ApprovedByUserId = me.Value;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("TRANSFER_RECEIVED", "StockTransfer", tr.TransferNo, null, 2);
            return Ok(new { id, message = $"{tr.TransferNo} received. Stock is now on the destination shelf." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"receive transfer {id}");
        }
    }

    // ══════════════════════ request bodies (part 2) ═════════════════════

    public record AdjustmentLineRequest(int ProductId, int NewQty);

    public record AdjustmentRequest(
        int LocationId, DateOnly? AdjustmentDate, int ReasonId, string? ReasonNotes,
        List<AdjustmentLineRequest> Lines);

    public record TransferLineRequest(int ProductId, int Qty);

    public record TransferRequest(
        int FromLocationId, int ToLocationId, DateOnly? TransferDate, string? Notes,
        List<TransferLineRequest> Lines);
}
