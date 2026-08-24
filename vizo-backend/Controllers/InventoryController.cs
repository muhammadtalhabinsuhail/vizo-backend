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
}
