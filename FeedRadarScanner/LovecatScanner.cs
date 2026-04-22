using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class LovecatScanner
{
    private readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const string Origin = "https://www.lovecat.com.tw";

    public async Task<List<Product>> ScanAsync(string collectionUrl, CancellationToken ct = default)
    {
        var handles = await GetAllProductHandlesAsync(collectionUrl, ct);
        Console.WriteLine($"[Debug] total handles = {handles.Count}");

        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct };
        var results = new List<Product>();
        var gate = new object();

        await Parallel.ForEachAsync(handles, options, async (handle, token) =>
        {
            try
            {
                var p = await FetchProductAsync(handle, token);
                if (p != null)
                {
                    lock (gate) results.Add(p);
                    Console.WriteLine($"[OK] {p.Title}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR] {handle} {ex.Message}");
            }
        });

        return results;
    }

    private async Task<List<string>> GetAllProductHandlesAsync(string collectionUrl, CancellationToken ct)
    {
        var url = BuildSearchProductsUrl(collectionUrl, page: 1, per: 1000);
        Console.WriteLine("[Debug] Fetch " + url);
        var json = await _http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var handles = new List<string>();
        foreach (var p in doc.RootElement.GetProperty("products").EnumerateArray())
        {
            if (p.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String)
            {
                var handle = h.GetString();
                if (!string.IsNullOrWhiteSpace(handle))
                    handles.Add(handle.Trim());
            }
        }
        return handles;
    }

    private static string BuildSearchProductsUrl(string collectionUrl, int page, int per)
    {
        var baseUrl = collectionUrl.TrimEnd('/');
        if (!baseUrl.EndsWith("/search_products.json", StringComparison.OrdinalIgnoreCase))
            baseUrl += "/search_products.json";
        return $"{baseUrl}?page={page}&per={per}&sort_by=&product_filters=%5B%5D&tags=";
    }

    private async Task<Product?> FetchProductAsync(string handle, CancellationToken ct)
    {
        var encodedHandle = Uri.EscapeDataString(handle);
        var res = await _http.GetAsync($"{Origin}/products/{encodedHandle}.json", ct);
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var (ingredientsText, nutritionText) = ExtractSections(root);
        var ingredients = ParseIngredients(ingredientsText);

        return new Product
        {
            Url = $"{Origin}/products/{encodedHandle}",
            Title = root.GetProperty("title").GetString() ?? "",
            IngredientsText = ingredientsText,
            NutritionText = nutritionText,
            Ingredients = ingredients,
            ProteinPct = ParseNutrientPct(nutritionText, "蛋白質"),
            FatPct = ParseNutrientPct(nutritionText, "脂肪"),
            FiberPct = ParseNutrientPct(nutritionText, "粗纖維"),
        };
    }

    // 切出「成分段落」和「營養分析段落」
    private static (string ingredients, string nutrition) ExtractSections(JsonElement root)
    {
        if (!root.TryGetProperty("other_descriptions", out var descs) ||
            descs.ValueKind != JsonValueKind.Array)
            return ("", "");

        foreach (var d in descs.EnumerateArray())
        {
            if (!d.TryGetProperty("body_html", out var body)) continue;
            var html = body.GetString() ?? "";
            if (!html.Contains("內容/成分")) continue;

            var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", " "));

            var ingIdx = text.IndexOf("內容/成分", StringComparison.Ordinal);
            if (ingIdx < 0) continue;

            // 找各種結束邊界，取最早出現的
            var endMarkers = new[] { "規格", "產地", "適用對象", "注意" };
            var endIdx = text.Length;
            foreach (var m in endMarkers)
            {
                var idx = text.IndexOf(m, ingIdx + 5, StringComparison.Ordinal);
                if (idx > ingIdx && idx < endIdx) endIdx = idx;
            }

            var fullSection = text.Substring(ingIdx + "內容/成分".Length, endIdx - ingIdx - "內容/成分".Length).Trim();

            // 在這段裡再切出「營養分析」
            var nutIdx = fullSection.IndexOf("營養分析", StringComparison.Ordinal);
            string ingredients, nutrition;
            if (nutIdx > 0)
            {
                ingredients = fullSection[..nutIdx].Trim();
                nutrition = fullSection[(nutIdx + "營養分析".Length)..].Trim();
            }
            else
            {
                ingredients = fullSection;
                nutrition = "";
            }

            return (ingredients, nutrition);
        }

        return ("", "");
    }

    // 把成分文字拆成個別成分名稱清單
    private static List<string> ParseIngredients(string ingredientsText)
    {
        if (string.IsNullOrWhiteSpace(ingredientsText)) return new List<string>();

        // 在「營養添加物」前截斷，後面是維生素礦物質不算主要成分
        var cutIdx = ingredientsText.IndexOf("營養添加物", StringComparison.Ordinal);
        var text = cutIdx > 0 ? ingredientsText[..cutIdx] : ingredientsText;

        return text
            .Split(new[] { '、', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('。', '　', ' '))
            // 移除全形與半形括號內容（如「最少4%」「FOS」等備註）
            .Select(s => Regex.Replace(s, @"[\(（][^)）]*[\)）]", "").Trim())
            .Where(s =>
                s.Length >= 2 &&
                s.Length <= 30 &&
                !s.Contains('%') &&     // 過濾掉「蛋白質26%」這類
                !s.Contains('：') &&    // 過濾掉「粗蛋白：22%」這類
                !s.Contains(':') &&
                !s.StartsWith("以"))    // 過濾掉「以下」等說明文字
            .Distinct()
            .ToList();
    }

    private static double? ParseNutrientPct(string text, string nutrientName)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var idx = text.IndexOf(nutrientName, StringComparison.Ordinal);
        if (idx < 0) return null;
        var match = Regex.Match(text[(idx + nutrientName.Length)..], @"(\d+\.?\d*)%");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : null;
    }
}

public class Product
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string IngredientsText { get; set; } = "";
    public string NutritionText { get; set; } = "";
    public List<string> Ingredients { get; set; } = new();
    public double? ProteinPct { get; set; }
    public double? FatPct { get; set; }
    public double? FiberPct { get; set; }
}
