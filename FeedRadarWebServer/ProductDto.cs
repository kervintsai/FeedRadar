public record IngredientDto(string Name, string Category);

public record BrandDto(string Brand, string BrandEn, string BrandZh, int ProductCount);

public record ProductDto(
    int Id,
    string Url,
    string Title,
    string Brand,
    string BrandEn,
    string BrandZh,
    string PetType,
    string LifeStage,
    bool IsPrescription,
    string IngredientsText,
    string NutritionText,
    double? ProteinPct,
    double? FatPct,
    double? FiberPct,
    string ScannedAt,
    string? CaloriesText = null,
    Dictionary<string, string>? Sections = null
);
