using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    private const int DefaultLimit = 24;
    private const int MaxLimit     = 100;

    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] string? brand,
        [FromQuery] string? ingredient,
        [FromQuery] string? petType,
        [FromQuery] string? form,
        [FromQuery] bool?   isPrescription,
        [FromQuery] int     page  = 1,
        [FromQuery] int     limit = DefaultLimit)
    {
        if (page < 1) page = 1;
        limit = Math.Clamp(limit, 1, MaxLimit);

        var key = $"products:{brand}:{ingredient}:{petType}:{form}:{isPrescription}:{page}:{limit}";
        if (!cache.TryGetValue(key, out ProductsPageDto? data))
        {
            var (products, total) = repo.GetAll(brand, ingredient, petType, form, isPrescription, page, limit);
            var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)limit);
            data = new ProductsPageDto(products, new PaginationDto(page, limit, total, totalPages));
            cache.Set(key, data, TimeSpan.FromMinutes(5));
        }

        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new ApiResponse<ProductsPageDto>(true, data));
    }
}
