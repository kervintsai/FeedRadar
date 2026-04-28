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
    List<FilterOption> Forms,
    List<FilterOption> IsPrescription
);

// ── Product ───────────────────────────────────────────────────────────────────
public record ProductDto(
    int     Id,
    string  Title,
    string  Brand,
    string  PetType,
    bool    IsPrescription,
    string  Form,
    string  IngredientsText,
    string  NutritionText,
    double? ProteinPct,
    double? FatPct,
    double? FiberPct,
    string? CaloriesText
);

public record PaginationDto(int Page, int Limit, int Total, int TotalPages);

public record ProductsPageDto(List<ProductDto> Products, PaginationDto Pagination);
