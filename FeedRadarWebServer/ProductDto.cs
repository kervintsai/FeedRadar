// ── Response envelope ─────────────────────────────────────────────────────────
public record ApiResponse<T>(bool Success, T? Data);
public record ApiError(string Code, string Message);
public record ApiErrorResponse(bool Success, ApiError Error);

// ── Filters ───────────────────────────────────────────────────────────────────
public record FilterOption(string Value, string Label, int Count);

public record FiltersDto(
    List<FilterOption> Brands,
    List<FilterOption> Ingredients,
    List<FilterOption> PetTypes,
    List<FilterOption> Ages,
    List<FilterOption> Forms,
    List<FilterOption> IsPrescription
);

// ── Price ─────────────────────────────────────────────────────────────────────
public record PriceVariantDto(string? Volume, decimal Price, string Site, string? UpdatedAt);

// ── Product ───────────────────────────────────────────────────────────────────
public record ProductDto(
    int           Id,
    string        Url,
    string        Title,
    string        Brand,
    string        PetType,
    string        Age,
    bool          IsPrescription,
    string        Form,
    List<string>  Images,
    List<string>  Ingredients,
    string        NutritionText,
    double?       ProteinPct,
    double?       FatPct,
    double?       CarbsPct,
    double?       PhosphorusPct,
    double?       CaloriesKcalPerKg,
    decimal?      Price,
    string?       PriceSource,
    string?       PriceUpdatedAt,
    List<PriceVariantDto> Variants,
    List<string>  Functional,
    bool?         IsGrainFree
);

public record PaginationDto(int Page, int Limit, int Total, int TotalPages);

public record ProductsPageDto(List<ProductDto> Products, PaginationDto Pagination);
