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

    private static readonly string[] NonFoodProductTypes =
        ["用品", "玩具", "貓砂", "清潔", "美容", "梳子", "衣服", "提包", "外出包"];

    private static readonly string[] NonFoodTitleKeywords =
        ["貓薄荷", "貓草", "薄荷草", "木天蓼", "逗貓棒", "貓砂"];

    private static bool IsNonFood(string title, string? productType)
    {
        if (!string.IsNullOrEmpty(productType) &&
            NonFoodProductTypes.Any(k => productType.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (NonFoodTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

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

    private static string DetectForm(string title, string productType, IEnumerable<string> tags, bool isTreatCollection = false)
    {
        var sources = new[] { title, productType }.Concat(tags);

        // Treat collections: check treat keywords first so wet-format treats (e.g. "肉泥餐包") aren't
        // misclassified as wet food; fall through to wet/dry only if no treat keyword matches.
        if (isTreatCollection)
        {
            foreach (var s in sources)
                if (s.Contains("肉乾") || s.Contains("凍乾") || s.Contains("冷凍乾燥") ||
                    s.Contains("零食") || s.Contains("點心") || s.Contains("補充條") ||
                    s.Contains("餅乾") || s.Contains("潔牙") || s.Contains("磨牙") ||
                    s.Contains("保健")) return "treat";
            return "treat"; // default for treat collections
        }

        foreach (var s in sources)
        {
            if (s.Contains("乾糧") || s.Contains("乾食")) return "dry";
            if (s.Contains("罐頭") || s.Contains("主食罐")) return "can";
            if (s.Contains("濕食") || s.Contains("餐包")) return "wet";
            if (s.Contains("肉乾") || s.Contains("凍乾") || s.Contains("冷凍乾燥") ||
                s.Contains("零食") || s.Contains("點心") || s.Contains("補充條") ||
                s.Contains("餅乾") || s.Contains("潔牙") || s.Contains("磨牙") ||
                s.Contains("保健")) return "treat";
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

        var isTreatCollection = collectionUrl.Contains("零食") || collectionUrl.Contains("點心");

        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct };
        var results = new List<Product>();
        var gate = new object();

        await Parallel.ForEachAsync(handles, options, async (handle, token) =>
        {
            try
            {
                var p = await FetchProductAsync(handle, isTreatCollection, token);
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

    private async Task<Product?> FetchProductAsync(string handle, bool isTreatCollection, CancellationToken ct)
    {
        var encodedHandle = Uri.EscapeDataString(handle);
        var res = await _http.GetAsync($"{Origin}/products/{encodedHandle}.json", ct);
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title       = root.GetProperty("title").GetString() ?? "";
        var productType = root.TryGetProperty("product_type", out var ptEl) ? ptEl.GetString() : null;

        if (IsNonFood(title, productType))
        {
            Console.WriteLine($"[Skip] non-food: {title}");
            return null;
        }

        // Shopify top-level metadata
        var vendor = root.TryGetProperty("vendor", out var vEl) ? vEl.GetString() : null;
        var tags = new List<string>();
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tagsEl.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");

        decimal? price = null;
        if (root.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.Number)
            price = priceEl.GetDecimal();

        // lovecat: featured_image is an object keyed by resolution (large/medium/original/…)
        string? imageUrl = null;
        if (root.TryGetProperty("featured_image", out var featuredEl) &&
            featuredEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var size in new[] { "large", "medium", "grande", "original" })
            {
                if (featuredEl.TryGetProperty(size, out var resEl) && resEl.ValueKind == JsonValueKind.String)
                {
                    var raw = resEl.GetString();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        imageUrl = raw.StartsWith("//") ? "https:" + raw : raw;
                        break;
                    }
                }
            }
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

        // Prefer dedicated nutrition section; fall back to ingredientsText for products
        // where guaranteed-analysis values are embedded in the ingredients block.
        var nutrientSource = !string.IsNullOrWhiteSpace(nutritionText) ? nutritionText : ingredientsText;

        var servingG    = ParseServingGrams(nutrientSource);
        var proteinPct  = ParseNutrientPct(nutrientSource, "蛋白質", "粗蛋白")
                       ?? ParseNutrientGramsAsPct(nutrientSource, servingG, "蛋白質", "蛋白");
        var fatPct      = ParseNutrientPct(nutrientSource, "脂肪")
                       ?? ParseNutrientGramsAsPct(nutrientSource, servingG, "脂肪");
        var fiberPct    = ParseNutrientPct(nutrientSource, "粗纖維")
                       ?? ParseNutrientGramsAsPct(nutrientSource, servingG, "纖維");
        var moisturePct = ParseNutrientPct(nutrientSource, "水分")
                       ?? ParseNutrientGramsAsPct(nutrientSource, servingG, "水分");
        var ashPct      = ParseNutrientPct(nutrientSource, "灰分", "灰份")
                       ?? ParseNutrientGramsAsPct(nutrientSource, servingG, "灰分", "灰份");

        // NFE carbs = 100 - protein - fat - fiber - moisture - ash (all must be present)
        double? carbsPct = null;
        if (proteinPct.HasValue && fatPct.HasValue && fiberPct.HasValue &&
            moisturePct.HasValue && ashPct.HasValue)
        {
            var c = 100 - proteinPct.Value - fatPct.Value - fiberPct.Value
                       - moisturePct.Value - ashPct.Value;
            carbsPct = Math.Round(Math.Max(c, 0), 2);
        }

        return new Product
        {
            Url             = $"{Origin}/products/{encodedHandle}",
            Title           = title,
            Brand           = ExtractBrand(title),
            PetType         = DetectPetType(title, productType ?? "", tags),
            AgeStage        = DetectAgeStage(title, tags),
            IsPrescription  = DetectIsPrescription(title, tags),
            Form            = DetectForm(title, productType ?? "", tags, isTreatCollection),
            ImageUrl        = imageUrl,
            IngredientsText = ingredientsText,
            NutritionText   = nutritionText,
            Ingredients     = ParseIngredients(ingredientsText),
            Sections        = sections,
            CaloriesText    = ParseCaloriesText(sections),
            ProteinPct      = proteinPct,
            FatPct          = fatPct,
            FiberPct        = fiberPct,
            MoisturePct     = moisturePct,
            AshPct          = ashPct,
            CarbsPct        = carbsPct,
            Price           = price,
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

    private static double? ParseNutrientPct(string text, params string[] names)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var name in names)
        {
            var idx = text.IndexOf(name, StringComparison.Ordinal);
            if (idx < 0) continue;
            var match = Regex.Match(text[(idx + name.Length)..], @"(\d+\.?\d*)%");
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                return val;
        }
        return null;
    }

    // Extracts serving size from patterns like "每30g含" or "每100公克"
    private static double? ParseServingGrams(string text)
    {
        var m = Regex.Match(text, @"每\s*(\d+\.?\d*)\s*(?:g|G|公克|克)");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var g) ? g : null;
    }

    // Converts nutrient gram values to percentage using serving size; returns null if result > 100
    private static double? ParseNutrientGramsAsPct(string text, double? servingG, params string[] names)
    {
        if (string.IsNullOrEmpty(text) || !servingG.HasValue || servingG <= 0) return null;
        foreach (var name in names)
        {
            var idx = text.IndexOf(name, StringComparison.Ordinal);
            if (idx < 0) continue;
            var match = Regex.Match(text[(idx + name.Length)..], @"(\d+\.?\d*)\s*g");
            if (!match.Success) continue;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var grams)) continue;
            var pct = Math.Round(grams / servingG.Value * 100, 2);
            if (pct > 100) return null;
            return pct;
        }
        return null;
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
    public double? MoisturePct  { get; set; }
    public double? AshPct       { get; set; }
    public double?   CarbsPct  { get; set; }
    public decimal?  Price     { get; set; }
}
