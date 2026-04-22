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

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = ct
        };

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
        var per = 1000;
        var url = BuildSearchProductsUrl(collectionUrl, page: 1, per);

        Console.WriteLine("[Debug] Fetch " + url);
        var json = await _http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var handles = new List<string>();
        foreach (var p in root.GetProperty("products").EnumerateArray())
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
        var jsonUrl = $"{Origin}/products/{encodedHandle}.json";

        var res = await _http.GetAsync(jsonUrl, ct);
        if (!res.IsSuccessStatusCode)
        {
            Console.WriteLine($"[HTTP {(int)res.StatusCode}] {jsonUrl}");
            return null;
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var (ingredients, nutrition) = ExtractSections(root);

        return new Product
        {
            Url = $"{Origin}/products/{encodedHandle}",
            Title = root.GetProperty("title").GetString() ?? "",
            IngredientsText = ingredients,
            NutritionText = nutrition,
            ProteinPct = ParseNutrientPct(nutrition, "蛋白質"),
            FatPct = ParseNutrientPct(nutrition, "脂肪"),
            FiberPct = ParseNutrientPct(nutrition, "粗纖維"),
        };
    }

    // 從 other_descriptions 找到含「內容/成分」的區塊，拆成成分和營養分析兩段
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

            var text = Regex.Replace(html, "<.*?>", string.Empty);
            text = System.Net.WebUtility.HtmlDecode(text);

            var ingIdx = text.IndexOf("內容/成分", StringComparison.Ordinal);
            if (ingIdx < 0) continue;

            var nutIdx = text.IndexOf("營養分析", StringComparison.Ordinal);

            string ingredients, nutrition;
            if (nutIdx > ingIdx)
            {
                ingredients = text.Substring(ingIdx + "內容/成分".Length, nutIdx - ingIdx - "內容/成分".Length).Trim();
                nutrition = text.Substring(nutIdx + "營養分析".Length).Trim();
            }
            else
            {
                ingredients = text.Substring(ingIdx + "內容/成分".Length).Trim();
                nutrition = "";
            }

            return (ingredients, nutrition);
        }

        return ("", "");
    }

    // 從營養分析文字中解析指定營養素的百分比數值
    private static double? ParseNutrientPct(string text, string nutrientName)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var idx = text.IndexOf(nutrientName, StringComparison.Ordinal);
        if (idx < 0) return null;

        var after = text.Substring(idx + nutrientName.Length);
        var match = Regex.Match(after, @"(\d+\.?\d*)%");
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            return val;

        return null;
    }
}

public class Product
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string IngredientsText { get; set; } = "";
    public string NutritionText { get; set; } = "";
    public double? ProteinPct { get; set; }
    public double? FatPct { get; set; }
    public double? FiberPct { get; set; }
}
