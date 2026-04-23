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
    Dictionary<string, string>? Sections = null
);
