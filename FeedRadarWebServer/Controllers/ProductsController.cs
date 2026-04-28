using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    private const int MaxLimit     = 100;
    private const int DefaultLimit = 24;

    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] string? type,
        [FromQuery] string? form,
        [FromQuery] string? age,
        [FromQuery] string? brand,
        [FromQuery] string? flavor,
        [FromQuery] string? func,
        [FromQuery] string? special,
        [FromQuery] int page  = 1,
        [FromQuery] int limit = DefaultLimit)
    {
        if (page < 1) page = 1;
        if (limit < 1 || limit > MaxLimit) limit = Math.Clamp(limit, 1, MaxLimit);

        var cacheKey = $"products:{type}:{form}:{age}:{brand}:{flavor}:{func}:{special}:{page}:{limit}";
        if (!cache.TryGetValue(cacheKey, out ProductsResponseData? data))
        {
            var (products, total) = repo.GetAll(type, form, age, brand, flavor, func, special, page, limit);
            var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)limit);
            data = new ProductsResponseData(
                products,
                new PaginationDto(page, limit, total, totalPages)
            );
            cache.Set(cacheKey, data, TimeSpan.FromMinutes(5));
        }

        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new ApiResponse<ProductsResponseData>(true, data));
    }
}
