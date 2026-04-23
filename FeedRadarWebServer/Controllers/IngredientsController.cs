using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

[ApiController]
[Route("api/[controller]")]
public class IngredientsController(ProductRepository repo, IMemoryCache cache) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        if (!cache.TryGetValue("ingredients", out List<IngredientDto>? ingredients))
        {
            ingredients = repo.GetIngredients();
            cache.Set("ingredients", ingredients, TimeSpan.FromMinutes(30));
        }
        return Ok(ingredients);
    }
}
