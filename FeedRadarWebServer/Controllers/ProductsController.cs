using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class ProductsController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] string? q,
        [FromQuery] List<string>? ingredient,
        [FromQuery] double? minProtein,
        [FromQuery] double? maxFat,
        [FromQuery] double? maxFiber,
        [FromQuery] string? brand,
        [FromQuery] string? lifeStage,
        [FromQuery] bool? isPrescription)
    {
        var cacheKey = $"products:{q}:{string.Join(",", ingredient ?? [])}:{minProtein}:{maxFat}:{maxFiber}:{brand}:{lifeStage}:{isPrescription}";
        if (!cache.TryGetValue(cacheKey, out List<ProductDto>? products))
        {
            products = repo.GetAll(q, ingredient, minProtein, maxFat, maxFiber, brand, lifeStage, isPrescription);
            cache.Set(cacheKey, products, CacheTtl);
        }
        return Ok(products);
    }

    [HttpGet("{id:int}")]
    public IActionResult GetById(int id)
    {
        var cacheKey = $"product:{id}";
        if (!cache.TryGetValue(cacheKey, out ProductDto? product))
        {
            product = repo.GetById(id);
            cache.Set(cacheKey, product, CacheTtl);
        }
        return product is null ? NotFound() : Ok(product);
    }
}
