using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class BrandsController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        if (!cache.TryGetValue("brands", out List<BrandDto>? brands))
        {
            brands = repo.GetBrands();
            cache.Set("brands", brands, TimeSpan.FromMinutes(30));
        }
        return Ok(brands);
    }
}
