// Filter endpoints
public record FilterOptionDto(string Value, string Label, int Count);

public record FiltersDto(
    List<FilterOptionDto> Types,
    List<FilterOptionDto> Forms,
    List<FilterOptionDto> Ages,
    List<FilterOptionDto> Brands,
    List<FilterOptionDto> Functional,
    List<FilterOptionDto> Special
);

// Legacy endpoints (kept for backward compat)
public record IngredientDto(string Name, string Category);
public record BrandDto(string Brand, string BrandEn, string BrandZh, int ProductCount);

// Product response (matches spec)
public record NutritionDto(
    string? Protein,
    string? Fat,
    string? Carbs,
    string? Phosphorus,
    string? Calories
);

public record ProductResponseDto(
    int Id,
    string Name,
    string Brand,
    string Type,
    string TypeLabel,
    string Form,
    string FormLabel,
    string Age,
    string AgeLabel,
    string? Flavor,
    string[] Functional,
    string[] Special,
    string? Volume,
    int? Price,
    string? Image,
    NutritionDto Nutrition
);

public record PaginationDto(int Page, int Limit, int Total, int TotalPages);

public record ProductsPageDto(List<ProductResponseDto> Products, PaginationDto Pagination);
