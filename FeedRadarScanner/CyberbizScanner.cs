using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

public class CyberbizScanner
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string SiteName { get; }
    protected string Origin { get; }

    public CyberbizScanner(string origin, string siteName)
    {
        Origin   = origin;
        SiteName = siteName;
    }

    // ── Section parsing ───────────────────────────────────────────────────────

    private static readonly (string Key, Regex Pattern)[] SectionDefs =
    {
        ("內容",    new Regex(@"內容(?:/成分)?",                RegexOptions.Compiled)),
        ("添加物",  new Regex(@"添加物(?:[（(][^）)]*[）)])?",  RegexOptions.Compiled)),
        ("營養成分", new Regex(@"營養(?:成分(?:及含量)?|分析)", RegexOptions.Compiled)),
        ("代謝能",  new Regex(@"代謝能",                        RegexOptions.Compiled)),
        ("適口性",  new Regex(@"適口性",                        RegexOptions.Compiled)),
        ("保存方式", new Regex(@"保存方式",                     RegexOptions.Compiled)),
    };

    private static readonly string[] BlockEndMarkers = { "規格", "產地", "適用對象", "注意" };

    private static readonly Regex AmountRegex = new(
        @"\s*\d+(?:[.,]\d+)?\s*(?:mg|g|mcg|μg|IU|iu)\s*/\s*\w+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PctRegex   = new(@"[（(](\d+\.?\d*)%[）)]", RegexOptions.Compiled);
    private static readonly Regex ParenRegex = new(@"[\(（][^)）]*[\)）]",     RegexOptions.Compiled);

    private static string HtmlToText(string html)
    {
        var withBreaks = Regex.Replace(html, @"</?p[^>]*>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(withBreaks, @"<[^>]+>", " "));
    }

    // ── Non-food filter ───────────────────────────────────────────────────────

    private static readonly string[] NonFoodProductTypes =
        ["用品", "玩具", "貓砂", "清潔", "美容", "梳子", "衣服", "提包", "外出包"];

    private static readonly string[] NonFoodTitleKeywords =
        ["貓薄荷", "貓草", "薄荷草", "木天蓼", "逗貓棒", "貓砂"];

    private static bool IsNonFood(string title, string? productType)
    {
        if (!string.IsNullOrEmpty(productType) &&
            NonFoodProductTypes.Any(k => productType.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        return NonFoodTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // ── Metadata detection ────────────────────────────────────────────────────

    private static string ExtractBrand(string title)
    {
        var m = Regex.Match(title, @"【([^】]+)】");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string DetectPetType(string title, string productType, IEnumerable<string> tags)
    {
        foreach (var s in new[] { title, productType }.Concat(tags))
        {
            if (s.Contains("犬") || s.Contains("狗")) return "dog";
            if (s.Contains("貓"))                     return "cat";
        }
        return "";
    }

    private static bool DetectIsPrescription(string title, IEnumerable<string> tags)
        => new[] { title }.Concat(tags).Any(s => s.Contains("處方"));

    private static string DetectForm(string title, string productType, IEnumerable<string> tags, bool isTreatCollection)
    {
        var sources = new[] { title, productType }.Concat(tags);

        if (isTreatCollection)
        {
            foreach (var s in sources)
                if (s.Contains("肉乾") || s.Contains("凍乾") || s.Contains("冷凍乾燥") ||
                    s.Contains("零食") || s.Contains("點心") || s.Contains("補充條") ||
                    s.Contains("餅乾") || s.Contains("潔牙") || s.Contains("磨牙") ||
                    s.Contains("保健")) return "treat";
            return "treat";
        }

        foreach (var s in sources)
        {
            if (s.Contains("乾糧") || s.Contains("乾食")) return "dry";
            if (s.Contains("罐頭") || s.Contains("主食罐") || s.Contains("罐罐")) return "can";
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
        foreach (var s in new[] { title }.Concat(tags))
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

    // ── Scan entry point ──────────────────────────────────────────────────────

    public async Task<List<Product>> ScanAsync(string collectionUrl, CancellationToken ct = default)
    {
        var handles = await GetAllProductHandlesAsync(collectionUrl, ct);
        Console.WriteLine($"[{SiteName}] {handles.Count} handles from {collectionUrl}");

        var isTreatCollection = collectionUrl.Contains("零食") || collectionUrl.Contains("點心");
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct };
        var results = new List<Product>();
        var gate    = new object();

        await Parallel.ForEachAsync(handles, options, async (handle, token) =>
        {
            try
            {
                var p = await FetchProductAsync(handle, isTreatCollection, token);
                if (p != null)
                {
                    lock (gate) results.Add(p);
                    Console.WriteLine($"[{SiteName}][OK] {p.Title}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{SiteName}][ERR] {handle} {ex.Message}");
            }
        });

        return results;
    }

    private async Task<List<string>> GetAllProductHandlesAsync(string collectionUrl, CancellationToken ct)
    {
        var handles = new List<string>();
        int page    = 1;

        while (true)
        {
            var url  = BuildSearchProductsUrl(collectionUrl, page, per: 250);
            var json = await _http.GetStringAsync(url, ct);

            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var p in root.GetProperty("products").EnumerateArray())
                if (p.TryGetProperty("handle", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    var handle = h.GetString();
                    if (!string.IsNullOrWhiteSpace(handle)) handles.Add(handle.Trim());
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
        var uri  = new Uri(collectionUrl.TrimEnd('/'));
        var path = uri.AbsolutePath;
        if (!path.EndsWith("/search_products.json", StringComparison.OrdinalIgnoreCase))
            path += "/search_products.json";
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
        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title       = root.GetProperty("title").GetString() ?? "";
        var productType = root.TryGetProperty("product_type", out var ptEl) ? ptEl.GetString() : null;

        if (IsNonFood(title, productType))
        {
            Console.WriteLine($"[{SiteName}][Skip] non-food: {title}");
            return null;
        }

        var tags = new List<string>();
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tagsEl.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");

        decimal? price = null;
        if (root.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.Number)
            price = priceEl.GetDecimal();

        string? imageUrl = null;
        if (root.TryGetProperty("featured_image", out var featuredEl) &&
            featuredEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var size in new[] { "large", "medium", "grande", "original" })
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

        var allSections = ExtractAllSections(root);
        var rawBlock    = allSections.GetValueOrDefault("內容", "");

        var paragraphs = rawBlock
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 2).ToList();

        var ingredientsText   = paragraphs.FirstOrDefault() ?? "";
        var embeddedNutrition = paragraphs.Count > 1 ? string.Join(" ", paragraphs.Skip(1)).Trim() : "";
        var nutritionText     = allSections.GetValueOrDefault("營養成分", embeddedNutrition);

        var sections = allSections.Where(kv => kv.Key != "內容").ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!string.IsNullOrWhiteSpace(embeddedNutrition) && !sections.ContainsKey("成分"))
            sections["成分"] = embeddedNutrition;

        var nutrientSource = !string.IsNullOrWhiteSpace(nutritionText) ? nutritionText : ingredientsText;
        var servingG       = ParseServingGrams(nutrientSource);

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

        double? carbsPct = null;
        if (proteinPct.HasValue && fatPct.HasValue && fiberPct.HasValue &&
            moisturePct.HasValue && ashPct.HasValue)
        {
            var c = 100 - proteinPct.Value - fatPct.Value - fiberPct.Value
                       - moisturePct.Value - ashPct.Value;
            carbsPct = Math.Round(Math.Max(c, 0), 2);
        }

        var phosphorusPct = ParseNutrientPct(nutrientSource, "磷");

        return new Product
        {
            Url                = $"{Origin}/products/{encodedHandle}",
            Title              = title,
            Brand              = ExtractBrand(title),
            PetType            = DetectPetType(title, productType ?? "", tags),
            Age                = DetectAgeStage(title, tags),
            IsPrescription     = DetectIsPrescription(title, tags),
            Form               = DetectForm(title, productType ?? "", tags, isTreatCollection),
            ImageUrl           = imageUrl,
            IngredientsText    = ingredientsText,
            NutritionText      = nutritionText,
            Ingredients        = ParseIngredients(ingredientsText),
            Sections           = sections,
            CaloriesKcalPerKg  = ParseCaloriesKcalPerKg(sections),
            ProteinPct         = proteinPct,
            FatPct             = fatPct,
            FiberPct           = fiberPct,
            MoisturePct        = moisturePct,
            AshPct             = ashPct,
            CarbsPct           = carbsPct,
            PhosphorusPct      = phosphorusPct,
            Volume             = ParseVolume(title),
            Price              = price,
        };
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

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

            var text   = HtmlToText(html);
            var startM = SectionDefs[0].Pattern.Match(text);
            if (!startM.Success) continue;

            var endIdx = text.Length;
            foreach (var m in BlockEndMarkers)
            {
                var idx = text.IndexOf(m, startM.Index + startM.Length, StringComparison.Ordinal);
                if (idx > startM.Index && idx < endIdx) endIdx = idx;
            }

            var block = text[startM.Index..endIdx];
            var hits  = new List<(int HeaderStart, int ContentStart, string Key)>();
            foreach (var (key, pattern) in SectionDefs)
            {
                var m = pattern.Match(block);
                if (m.Success) hits.Add((m.Index, m.Index + m.Length, key));
            }
            hits.Sort((a, b) => a.HeaderStart.CompareTo(b.HeaderStart));

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

    private static readonly string[] IngredientPrefixes =
        ["冷凍乾燥", "凍乾", "去骨", "脫水", "乾燥", "烘乾", "新鮮", "冷凍", "有機"];

    private static string ComputeBaseName(string name)
    {
        foreach (var prefix in IngredientPrefixes)
            if (name.StartsWith(prefix) && name.Length - prefix.Length >= 2)
                return name[prefix.Length..];
        return name;
    }

    private static List<Ingredient> ParseIngredients(string ingredientsText)
    {
        if (string.IsNullOrWhiteSpace(ingredientsText)) return new();

        var cutIdx = ingredientsText.IndexOf("營養添加物", StringComparison.Ordinal);
        var text   = cutIdx > 0 ? ingredientsText[..cutIdx] : ingredientsText;

        var seen   = new HashSet<string>();
        var result = new List<Ingredient>();

        foreach (var raw in text.Split(new[] { '、', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim().Trim('。', '　', ' ');
            if (string.IsNullOrWhiteSpace(s)) continue;

            double? pct = null;
            var pctMatch = PctRegex.Match(s);
            if (pctMatch.Success &&
                double.TryParse(pctMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                pct = p;

            string? amountText = null;
            var amtMatch = AmountRegex.Match(s);
            if (amtMatch.Success) amountText = amtMatch.Value.Trim();

            var name     = AmountRegex.Replace(PctRegex.Replace(s, ""), "").Trim();
            var baseName = ComputeBaseName(ParenRegex.Replace(name, "").Trim());

            if (name.Length < 2 || name.Length > 50) continue;
            if (name.Contains('%') || name.Contains('：') || name.Contains(':')) continue;
            if (name.StartsWith("以")) continue;
            if (!seen.Add(name)) continue;

            result.Add(new Ingredient(name, pct, baseName, amountText));
        }

        return result;
    }

    private static double? ParseCaloriesKcalPerKg(Dictionary<string, string> sections)
    {
        foreach (var text in sections.Values)
        {
            var idx = text.IndexOf("熱量", StringComparison.Ordinal);
            if (idx < 0) continue;
            var segment = text[idx..Math.Min(idx + 60, text.Length)];
            var m = Regex.Match(segment, @"(\d+(?:\.\d+)?)\s*(?:大卡|kcal|Kcal|KCAL)",
                RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var val))
                return val;
        }
        return null;
    }

    private static string? ParseVolume(string title)
    {
        var m = Regex.Match(title, @"(\d+(?:\.\d+)?)\s*(kg|g|ml|mL|L|公克|克)\b",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : null;
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

    private static double? ParseServingGrams(string text)
    {
        var m = Regex.Match(text, @"每\s*(\d+\.?\d*)\s*(?:g|G|公克|克)");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var g) ? g : null;
    }

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
    public string  Url             { get; set; } = "";
    public string  Title           { get; set; } = "";
    public string  Brand           { get; set; } = "";
    public string  PetType         { get; set; } = "";
    public string  Age             { get; set; } = "";
    public bool    IsPrescription  { get; set; }
    public string  Form            { get; set; } = "";
    public string? ImageUrl        { get; set; }
    public string  IngredientsText { get; set; } = "";
    public string  NutritionText   { get; set; } = "";
    public List<Ingredient>           Ingredients { get; set; } = new();
    public Dictionary<string, string> Sections    { get; set; } = new();
    public double?  CaloriesKcalPerKg { get; set; }
    public double?  ProteinPct        { get; set; }
    public double?  FatPct            { get; set; }
    public double?  FiberPct          { get; set; }
    public double?  MoisturePct       { get; set; }
    public double?  AshPct            { get; set; }
    public double?  CarbsPct          { get; set; }
    public double?  PhosphorusPct     { get; set; }
    public string?  Volume            { get; set; }
    public decimal? Price             { get; set; }
}
