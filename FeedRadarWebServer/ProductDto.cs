public record IngredientDto(string Name, string Category);

public record ProductDto(
    int Id,
    string Url,
    string Title,
    string IngredientsText,
    string NutritionText,
    double? ProteinPct,
    double? FatPct,
    double? FiberPct,
    string ScannedAt,
    string? CaloriesText = null,
    Dictionary<string, string>? Sections = null
);
