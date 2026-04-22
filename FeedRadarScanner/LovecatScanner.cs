using System;
using System.Collections.Generic;
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

    // ====== 對外入口 ======
    public async Task<List<Product>> ScanAsync(string collectionUrl, CancellationToken ct = default)
    {
        var products = new List<Product>();

        // 1️⃣ 抓所有 product handles
        var handles = await GetAllProductHandlesAsync(collectionUrl, ct);
        Console.WriteLine($"[Debug] total handles = {handles.Count}");


        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 8, // 先從 4~8 試，太大容易 429
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

    // ====== Step 1: 抓 search_products.json ======
    private async Task<List<string>> GetAllProductHandlesAsync(string collectionUrl, CancellationToken ct)
    {
        var per = 1000;
        var url = BuildSearchProductsUrl(collectionUrl, page: 1, per);

        Console.WriteLine("[Debug] Fetch " + url);
        var json = await _http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var products = root.GetProperty("products");
        var handles = new List<string>();

        foreach (var p in products.EnumerateArray())
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

    // ====== Step 2: 抓單一產品 JSON ======
    private async Task<Product?> FetchProductAsync(string handle, CancellationToken ct)
    {
        // 中文 handle 一定要 encode
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

        var product = new Product
        {
            Url = $"{Origin}/products/{encodedHandle}",
            Title = root.GetProperty("title").GetString() ?? ""
        };

        product.IngredientsText = ExtractIngredients(root);
        return product;
    }

    // ====== Step 3: 從 other_descriptions 抓「內容/成分」 ======
    private static string ExtractIngredients(JsonElement root)
    {
        if (!root.TryGetProperty("other_descriptions", out var descs) ||
            descs.ValueKind != JsonValueKind.Array)
            return "";

        foreach (var d in descs.EnumerateArray())
        {
            if (!d.TryGetProperty("body_html", out var body)) continue;

            var html = body.GetString() ?? "";
            if (!html.Contains("內容/成分")) continue;

            // 去 HTML tag
            var text = Regex.Replace(html, "<.*?>", string.Empty);
            text = System.Net.WebUtility.HtmlDecode(text);

            // 只取「內容/成分」之後
            var idx = text.IndexOf("內容/成分", StringComparison.Ordinal);
            if (idx >= 0)
                return text.Substring(idx).Trim();
        }

        return "";
    }
}

// ====== 資料模型 ======
public class Product
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string IngredientsText { get; set; } = "";
}
