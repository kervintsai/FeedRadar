using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] int           page    = 1,
        [FromQuery] int           limit   = 24,
        [FromQuery] List<string>? type    = null,
        [FromQuery] string?       form    = null,
        [FromQuery] List<string>? age     = null,
        [FromQuery] List<string>? brand   = null,
        [FromQuery] List<string>? flavor  = null,
        [FromQuery] List<string>? func    = null,
        [FromQuery] List<string>? special = null)
    {
        var key = $"products:{page}:{limit}:{string.Join(",", type ?? [])}:{form}:" +
                  $"{string.Join(",", age ?? [])}:{string.Join(",", brand ?? [])}:" +
                  $"{string.Join(",", flavor ?? [])}:{string.Join(",", func ?? [])}:{string.Join(",", special ?? [])}";

        if (!cache.TryGetValue(key, out ProductsPageDto? page_))
        {
            var (items, total) = repo.GetAll(page, limit, type, form, age, brand, flavor, func, special);
            var totalPages = (int)Math.Ceiling((double)total / limit);
            page_ = new ProductsPageDto(items, new PaginationDto(page, limit, total, totalPages));
            cache.Set(key, page_, CacheTtl);
        }
        return Ok(ApiResponse<ProductsPageDto>.Ok(page_!));
    }

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id)
    {
        var key = $"product:{id}";
        if (!cache.TryGetValue(key, out ProductResponseDto? product))
        {
            product = repo.GetById(id);
            cache.Set(key, product, CacheTtl);
        }
        return product is null ? NotFound(new ApiErrorResponse(false, new ApiError("NOT_FOUND", "Product not found")))
                               : Ok(ApiResponse<ProductResponseDto>.Ok(product));
    }
}
