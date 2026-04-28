using System.Text.Json.Serialization;

// ── Response envelope ─────────────────────────────────────────────────────────
public record ApiResponse<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("data")]    T?   Data
);

public record ApiErrorResponse(
    [property: JsonPropertyName("success")] bool         Success,
    [property: JsonPropertyName("error")]   ApiErrorBody Error
);

public record ApiErrorBody(
    [property: JsonPropertyName("code")]    string Code,
    [property: JsonPropertyName("message")] string Message
);

// ── Filters ───────────────────────────────────────────────────────────────────
public record FilterItemDto(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("count")] int    Count
);

public record FiltersData(
    List<FilterItemDto> Types,
    List<FilterItemDto> Forms,
    List<FilterItemDto> Ages,
    List<FilterItemDto> Brands,
    List<FilterItemDto> Flavors,
    List<FilterItemDto> Functional,
    List<FilterItemDto> Special
);

// ── Product ───────────────────────────────────────────────────────────────────
public record NutritionDto(
    [property: JsonPropertyName("protein")]    string? Protein,
    [property: JsonPropertyName("fat")]        string? Fat,
    [property: JsonPropertyName("fiber")]      string? Fiber,
    [property: JsonPropertyName("carbs")]      string? Carbs,
    [property: JsonPropertyName("phosphorus")] string? Phosphorus,
    [property: JsonPropertyName("calories")]   string? Calories
);

public record ApiProductDto(
    [property: JsonPropertyName("id")]         string         Id,
    [property: JsonPropertyName("name")]       string         Name,
    [property: JsonPropertyName("brand")]      string?        Brand,
    [property: JsonPropertyName("type")]       string?        Type,
    [property: JsonPropertyName("typeLabel")]  string?        TypeLabel,
    [property: JsonPropertyName("form")]       string?        Form,
    [property: JsonPropertyName("formLabel")]  string?        FormLabel,
    [property: JsonPropertyName("age")]        string?        Age,
    [property: JsonPropertyName("ageLabel")]   string?        AgeLabel,
    [property: JsonPropertyName("flavors")]    List<string>   Flavors,
    [property: JsonPropertyName("functional")] List<string>   Functional,
    [property: JsonPropertyName("special")]    List<string>   Special,
    [property: JsonPropertyName("volume")]     string?        Volume,
    [property: JsonPropertyName("price")]      int?           Price,
    [property: JsonPropertyName("image")]      string?        Image,
    [property: JsonPropertyName("nutrition")]  NutritionDto   Nutrition
);

public record PaginationDto(
    [property: JsonPropertyName("page")]       int Page,
    [property: JsonPropertyName("limit")]      int Limit,
    [property: JsonPropertyName("total")]      int Total,
    [property: JsonPropertyName("totalPages")] int TotalPages
);

public record ProductsResponseData(
    [property: JsonPropertyName("products")]   List<ApiProductDto> Products,
    [property: JsonPropertyName("pagination")] PaginationDto       Pagination
);

// ── Legacy (kept for /api/ingredients only) ───────────────────────────────────
public record ProductDto(
    int Id, string Url, string Title,
    string IngredientsText, string NutritionText,
    double? ProteinPct, double? FatPct, double? FiberPct,
    string ScannedAt,
    string? CaloriesText = null,
    Dictionary<string, string>? Sections = null
);
