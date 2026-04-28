using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class FiltersController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        if (!cache.TryGetValue("filters", out FiltersData? data))
        {
            data = repo.GetFilters();
            cache.Set("filters", data, TimeSpan.FromHours(1));
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(new ApiResponse<object>(true, new
        {
            types      = data!.Types,
            forms      = data.Forms,
            ages       = data.Ages,
            brands     = data.Brands,
            flavors    = data.Flavors,
            functional = data.Functional,
            special    = data.Special,
        }));
    }
}
