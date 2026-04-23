using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class FiltersController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        if (!cache.TryGetValue("filters", out FiltersDto? filters))
        {
            filters = repo.GetFilters();
            cache.Set("filters", filters, TimeSpan.FromHours(1));
        }
        return Ok(ApiResponse<FiltersDto>.Ok(filters!));
    }
}
