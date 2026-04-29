using System;
using System.Collections.Generic;
using System.Globalization;
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

    private int _debugCount = 0;

    private const string Origin = "https://www.lovecat.com.tw";

    // Section header patterns in order of expected appearance.
    // "內容" matches "內容" or "內容/成分"; "成分" is NOT a separate header —
    // it is extracted by splitting the 內容 block at the first nutrition keyword.
    private static readonly (string Key, Regex Pattern)[] SectionDefs =
    {
        ("內容",    new Regex(@"內容(?:/成分)?",                 RegexOptions.Compiled)),
        ("添加物",  new Regex(@"添加物(?:[（(][^）)]*[）)])?",   RegexOptions.Compiled)),
        ("營養成分", new Regex(@"營養(?:成分(?:及含量)?|分析)",  RegexOptions.Compiled)),
        ("代謝能",  new Regex(@"代謝能",                         RegexOptions.Compiled)),
        ("適口性",  new Regex(@"適口性",                         RegexOptions.Compiled)),
        ("保存方式", new Regex(@"保存方式",                      RegexOptions.Compiled)),
    };

    private static readonly string[] BlockEndMarkers = { "規格", "產地", "適用對象", "注意" };

    // Matches weight-based concentrations: 1000mg/kg, 0.5g/kg, 50 IU/kg, etc.
    private static readonly Regex AmountRegex = new(
        @"\s*\d+(?:[.,]\d+)?\s*(?:mg|g|mcg|μg|IU|iu)\s*/\s*\w+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Converts HTML to text, preserving <p>/<br> boundaries as newlines so paragraph
    // structure can be used to separate ingredient list from nutrition data.
    private static string HtmlToText(string html)
    {
        var withBreaks = Regex.Replace(html, @"</?p[^>]*>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(withBreaks, @"<[^>]+>", " "));
    }
    private static readonly Regex PctRegex   = new(@"[（(](\d+\.?\d*)%[）)]", RegexOptions.Compiled);
    private static readonly Regex ParenRegex = new(@"[\(（][^)）]*[\)）]",     RegexOptions.Compiled);

    // ── Metadata extraction ───────────────────────────────────────────────────

    private static string ExtractBrand(string title)
    {
        var m = Regex.Match(title, @"【([^】]+)】");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string DetectPetType(string title, string productType, IEnumerable<string> tags)
    {
        var sources = new[] { title, productType }.Concat(tags);
        foreach (var s in sources)
        {
            if (s.Contains("犬") || s.Contains("狗")) return "dog";
            if (s.Contains("貓"))                     return "cat";
        }
        return "";
    }

    private static bool DetectIsPrescription(string title, IEnumerable<string> tags)
        => new[] { title }.Concat(tags).Any(s => s.Contains("處方"));

    private static string DetectForm(string title, string productType, IEnumerable<string> tags)
    {
        var sources = new[] { title, productType }.Concat(tags);
        foreach (var s in sources)
        {
            if (s.Contains("乾糧") || s.Contains("乾食")) return "dry";
            if (s.Contains("濕食") || s.Contains("主食罐") || s.Contains("罐頭") || s.Contains("餐包")) return "wet";
        }
        return "";
    }

    private static string DetectAgeStage(string title, IEnumerable<string> tags)
    {
        var sources = new[] { title }.Concat(tags);
        foreach (var s in sources)
        {
            if (s.Contains("幼犬") || s.Contains("幼貓") || s.Contains("幼兔") ||
                s.Contains("幼齡") || s.Contains("幼年") || s.Contains("成長期") || s.Contains("離乳"))
                return "puppy";
            if (s.Contains("老犬") || s.Contains("老貓") || s.Contains("老兔") ||
                s.Contains("高齡") || s.Contains("熟齡") || s.Contains("老年") || s.Contains("銀髮"))
                return "senior";
        }
        return "adult";
    }

    // Longest prefixes first so "凍乾" isn't masked by a hypothetical single-char match
    private static readonly string[] IngredientPrefixes =
        { "冷凍乾燥", "凍乾", "去骨", "脫水", "乾燥", "烘乾", "新鮮", "冷凍", "有機" };

    private static string ComputeBaseName(string name)
    {
        foreach (var prefix in IngredientPrefixes)
            if (name.StartsWith(prefix) && name.Length - prefix.Length >= 2)
                return name[prefix.Length..];
        return name;
    }

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
        var handles = new List<string>();
        int page = 1;

        while (true)
        {
            var url = BuildSearchProductsUrl(collectionUrl, page, per: 250);
            Console.WriteLine($"[Debug] Fetch page {page}: {url}");
            var json = await _http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var p in root.GetProperty("products").EnumerateArray())
            {
                if (p.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    var handle = h.GetString();
                    if (!string.IsNullOrWhiteSpace(handle))
                        handles.Add(handle.Trim());
                }
            }

            if (!root.TryGetProperty("total_pages", out var totalPagesEl) ||
                page >= totalPagesEl.GetInt32())
                break;

            page++;
        }

        return handles;
    }

    private static string BuildSearchProductsUrl(string collectionUrl, int page, int per)
    {
        var uri = new Uri(collectionUrl.TrimEnd('/'));
        var path = uri.AbsolutePath;
        if (!path.EndsWith("/search_products.json", StringComparison.OrdinalIgnoreCase))
            path += "/search_products.json";
        // Encode each segment individually to handle Chinese characters safely
        var encodedPath = string.Join("/",
            path.Split('/').Select(s => Uri.EscapeDataString(Uri.UnescapeDataString(s))));
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}{encodedPath}?page={page}&per={per}&sort_by=&product_filters=%5B%5D&tags=";
    }

    private async Task<Product?> FetchProductAsync(string handle, CancellationToken ct)
    {
        var encodedHandle = Uri.EscapeDataString(handle);
        var res = await _http.GetAsync($"{Origin}/products/{encodedHandle}.json", ct);
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.GetProperty("title").GetString() ?? "";

        // Shopify top-level metadata
        var vendor      = root.TryGetProperty("vendor",       out var vEl)  ? vEl.GetString()  : null;
        var productType = root.TryGetProperty("product_type", out var ptEl) ? ptEl.GetString() : null;
        var tags = new List<string>();
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tagsEl.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");

        // Extract first product image URL (lovecat uses photo_urls or featured_image, not images)
        string? imageUrl = null;
        if (root.TryGetProperty("photo_urls", out var photoUrlsEl) && photoUrlsEl.ValueKind == JsonValueKind.Array)
        {
            var first = photoUrlsEl.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.String)
                imageUrl = first.GetString();
        }
        if (imageUrl == null && root.TryGetProperty("featured_image", out var featuredEl))
        {
            if (featuredEl.ValueKind == JsonValueKind.String)
                imageUrl = featuredEl.GetString();
            else if (featuredEl.ValueKind == JsonValueKind.Object && featuredEl.TryGetProperty("src", out var srcEl))
                imageUrl = srcEl.GetString();
        }

        // Debug: print root keys + image result for first 3 products
        if (_debugCount < 3)
        {
            var keys = root.EnumerateObject().Select(p => p.Name).ToList();
            Console.WriteLine($"[Debug:keys] {handle} → [{string.Join(", ", keys)}]");
            Console.WriteLine($"[Debug:image] imageUrl={imageUrl ?? "null"}");
            Console.WriteLine($"[Debug:ageStage] title={title} tags=[{string.Join(",", tags)}]");
            Interlocked.Increment(ref _debugCount);
        }

        var allSections = ExtractAllSections(root);
        var rawBlock    = allSections.GetValueOrDefault("內容", "");

        var paragraphs = rawBlock
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 2)
            .ToList();

        var ingredientsText   = paragraphs.FirstOrDefault() ?? "";
        var embeddedNutrition = paragraphs.Count > 1
            ? string.Join(" ", paragraphs.Skip(1)).Trim()
            : "";

        var nutritionText = allSections.GetValueOrDefault("營養成分", embeddedNutrition);

        var sections = allSections
            .Where(kv => kv.Key != "內容")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!string.IsNullOrWhiteSpace(embeddedNutrition) && !sections.ContainsKey("成分"))
            sections["成分"] = embeddedNutrition;

        return new Product
        {
            Url             = $"{Origin}/products/{encodedHandle}",
            Title           = title,
            Brand           = ExtractBrand(title),
            PetType         = DetectPetType(title, productType ?? "", tags),
            AgeStage        = DetectAgeStage(title, tags),
            IsPrescription  = DetectIsPrescription(title, tags),
            Form            = DetectForm(title, productType ?? "", tags),
            ImageUrl        = imageUrl,
            IngredientsText = ingredientsText,
            NutritionText   = nutritionText,
            Ingredients     = ParseIngredients(ingredientsText),
            Sections        = sections,
            CaloriesText    = ParseCaloriesText(sections),
            ProteinPct      = ParseNutrientPct(nutritionText, "蛋白質"),
            FatPct          = ParseNutrientPct(nutritionText, "脂肪"),
            FiberPct        = ParseNutrientPct(nutritionText, "粗纖維"),
        };
    }

    // Splits product HTML into named sections, e.g. "內容/成分", "添加物", "營養成分", "代謝能"
    private static Dictionary<string, string> ExtractAllSections(JsonElement root)
    {
        if (!root.TryGetProperty("other_descriptions", out var descs) ||
            descs.ValueKind != JsonValueKind.Array)
            return new();

        foreach (var d in descs.EnumerateArray())
        {
            if (!d.TryGetProperty("body_html", out var body)) continue;
            var html = body.GetString() ?? "";
            if (!html.Contains("內容")) continue;

            var text = HtmlToText(html);

            // Find where the nutritional info block starts
            var startM = SectionDefs[0].Pattern.Match(text);
            if (!startM.Success) continue;

            // Find where the block ends (spec, origin, warnings, etc.)
            var endIdx = text.Length;
            foreach (var m in BlockEndMarkers)
            {
                var idx = text.IndexOf(m, startM.Index + startM.Length, StringComparison.Ordinal);
                if (idx > startM.Index && idx < endIdx) endIdx = idx;
            }

            var block = text[startM.Index..endIdx];

            // Find all known section headers within the block
            var hits = new List<(int HeaderStart, int ContentStart, string Key)>();
            foreach (var (key, pattern) in SectionDefs)
            {
                var m = pattern.Match(block);
                if (m.Success)
                    hits.Add((m.Index, m.Index + m.Length, key));
            }
            hits.Sort((a, b) => a.HeaderStart.CompareTo(b.HeaderStart));

            // Content for section[i] spans from ContentStart to the next section's HeaderStart
            var result = new Dictionary<string, string>();
            for (int i = 0; i < hits.Count; i++)
            {
                var contentEnd = i + 1 < hits.Count ? hits[i + 1].HeaderStart : block.Length;
                result[hits[i].Key] = block[hits[i].ContentStart..contentEnd].Trim();
            }

            return result;
        }

        return new();
    }

    // Splits ingredient text into individual items, extracting name, percentage, and amount
    private static List<Ingredient> ParseIngredients(string ingredientsText)
    {
        if (string.IsNullOrWhiteSpace(ingredientsText)) return new();

        // Stop before additive/supplement sub-section if it appears inline
        var cutIdx = ingredientsText.IndexOf("營養添加物", StringComparison.Ordinal);
        var text = cutIdx > 0 ? ingredientsText[..cutIdx] : ingredientsText;

        var seen   = new HashSet<string>();
        var result = new List<Ingredient>();

        foreach (var raw in text.Split(new[] { '、', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim().Trim('。', '　', ' ');
            if (string.IsNullOrWhiteSpace(s)) continue;

            // Extract percentage: "去骨雞肉 (20%)" → 20.0
            double? pct = null;
            var pctMatch = PctRegex.Match(s);
            if (pctMatch.Success &&
                double.TryParse(pctMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                pct = p;

            // Extract weight amount: "葡萄糖胺 1000mg/kg" → "1000mg/kg"
            string? amountText = null;
            var amtMatch = AmountRegex.Match(s);
            if (amtMatch.Success) amountText = amtMatch.Value.Trim();

            // Name: remove only the percentage paren and amount; KEEP other parens
            // e.g. "甘露寡糖(萃取自酵母)" stays intact
            var name = AmountRegex.Replace(PctRegex.Replace(s, ""), "").Trim();

            // BaseName: strip ALL parens then apply prefix stripping
            // e.g. "甘露寡糖(萃取自酵母)" → "甘露寡糖"
            var baseName = ComputeBaseName(ParenRegex.Replace(name, "").Trim());

            if (name.Length < 2 || name.Length > 50) continue;
            if (name.Contains('%') || name.Contains('：') || name.Contains(':')) continue;
            if (name.StartsWith("以")) continue;
            if (!seen.Add(name)) continue;

            result.Add(new Ingredient(name, pct, baseName, amountText));
        }

        return result;
    }

    // Searches all section text for a "熱量" entry and returns the value string (e.g. "4325大卡")
    private static string? ParseCaloriesText(Dictionary<string, string> sections)
    {
        foreach (var text in sections.Values)
        {
            var idx = text.IndexOf("熱量", StringComparison.Ordinal);
            if (idx < 0) continue;
            var start = idx + 2;
            while (start < text.Length && text[start] is '：' or ':' or ' ')
                start++;
            var end = text.IndexOfAny(new[] { '\n', '、', '\r', '；' }, start);
            if (end < 0) end = Math.Min(start + 40, text.Length);
            var cal = text[start..end].Trim();
            if (cal.Length > 0) return cal;
        }
        return null;
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

public record Ingredient(string Name, double? Percentage, string BaseName, string? AmountText);

public class Product
{
    public string Url             { get; set; } = "";
    public string Title           { get; set; } = "";
    public string Brand           { get; set; } = "";
    public string PetType         { get; set; } = "";   // "cat" | "dog" | ""
    public string AgeStage        { get; set; } = "";   // "puppy" | "senior" | ""
    public bool   IsPrescription  { get; set; }
    public string Form            { get; set; } = "";   // "wet" | "dry" | ""
    public string? ImageUrl       { get; set; }
    public string IngredientsText { get; set; } = "";
    public string NutritionText   { get; set; } = "";
    public List<Ingredient>           Ingredients { get; set; } = new();
    public Dictionary<string, string> Sections    { get; set; } = new();
    public string? CaloriesText { get; set; }
    public double? ProteinPct   { get; set; }
    public double? FatPct       { get; set; }
    public double? FiberPct     { get; set; }
}
