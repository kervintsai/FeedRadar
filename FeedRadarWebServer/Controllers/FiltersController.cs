using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class FiltersController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        if (!cache.TryGetValue("filters", out FiltersDto? data))
        {
            data = repo.GetFilters();
            cache.Set("filters", data, TimeSpan.FromHours(1));
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(new ApiResponse<FiltersDto>(true, data));
    }
}
