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
    List<FilterOption> AgeStages,
    List<FilterOption> Forms,
    List<FilterOption> IsPrescription
);

// ── Price ─────────────────────────────────────────────────────────────────────
public record PriceDto(string Site, decimal Price, string Currency, string Url);

// ── Product ───────────────────────────────────────────────────────────────────
public record ProductDto(
    int     Id,
    string  Url,
    string  Title,
    string  Brand,
    string  PetType,
    string  AgeStage,
    bool    IsPrescription,
    string  Form,
    string? ImageUrl,
    string  IngredientsText,
    string  NutritionText,
    double? ProteinPct,
    double? FatPct,
    double? FiberPct,
    double? MoisturePct,
    double? AshPct,
    double? CarbsPct,
    string? CaloriesText,
    decimal? MinPrice,
    decimal? MaxPrice,
    List<PriceDto> Prices
);

public record PaginationDto(int Page, int Limit, int Total, int TotalPages);

public record ProductsPageDto(List<ProductDto> Products, PaginationDto Pagination);
