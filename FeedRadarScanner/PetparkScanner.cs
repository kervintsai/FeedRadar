using HtmlAgilityPack;
using System.Globalization;
using System.Text.RegularExpressions;

public class PetparkScanner
{
    private readonly HttpClient _http;
    public string SiteName => "petpark";
    private const string Origin = "https://shop.petpark.com.tw";

    public PetparkScanner()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36");
    }

    // ── Form hint from collection URL ─────────────────────────────────────────

    private static string FormHint(string collectionUrl)
    {
        if (collectionUrl.Contains("/dryfood"))  return "dry";
        if (collectionUrl.Contains("/treats"))   return "treat";
        if (collectionUrl.Contains("/cans"))
            return collectionUrl.Contains("pouchfood") || collectionUrl.Contains("treats-") ? "wet" : "can";
        return "";
    }

    // ── Scan entry point ──────────────────────────────────────────────────────

    public async Task<List<Product>> ScanAsync(string collectionUrl, CancellationToken ct = default)
    {
        var productUrls = await GetAllProductUrlsAsync(collectionUrl, ct);
        Console.WriteLine($"[{SiteName}] {productUrls.Count} products from {collectionUrl}");

        var formHint = FormHint(collectionUrl);
        var results  = new List<Product>();

        foreach (var url in productUrls)
        {
            try
            {
                var product = await FetchProductAsync(url, formHint, ct);
                if (product != null)
                {
                    results.Add(product);
                    Console.WriteLine($"[{SiteName}][OK] {product.Title}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{SiteName}][ERR] {url} {ex.Message}");
            }
            await Task.Delay(300, ct);
        }

        return results;
    }

    // ── Listing page: collect all product URLs ────────────────────────────────

    private async Task<List<string>> GetAllProductUrlsAsync(string collectionUrl, CancellationToken ct)
    {
        var urls       = new List<string>();
        int totalPages = 1;

        for (int page = 1; page <= totalPages; page++)
        {
            var pageUrl = page == 1 ? collectionUrl : $"{collectionUrl}?p={page}";
            var html    = await _http.GetStringAsync(pageUrl, ct);
            var doc     = new HtmlDocument();
            doc.LoadHtml(html);

            if (page == 1)
                totalPages = ParseTotalPages(doc);

            var links = doc.DocumentNode.SelectNodes(
                "//a[contains(@class,'product-item-link') and @href]");

            if (links != null)
                foreach (var link in links)
                {
                    var href = link.GetAttributeValue("href", "");
                    if (!string.IsNullOrEmpty(href))
                        urls.Add(href.StartsWith("http") ? href : Origin + href);
                }
        }

        return urls.Distinct().ToList();
    }

    private static int ParseTotalPages(HtmlDocument doc)
    {
        // "項目 1 - 50，共 579 個" lives in <p class="toolbar-amount">
        var node = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'toolbar-amount')]");
        if (node != null)
        {
            var m = Regex.Match(node.InnerText, @"共\s*([\d,]+)\s*個");
            if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var total))
                return (int)Math.Ceiling(total / 50.0);
        }
        return 1;
    }

    // ── Product detail page ───────────────────────────────────────────────────

    private async Task<Product?> FetchProductAsync(string url, string formHint, CancellationToken ct)
    {
        var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;

        var html = await res.Content.ReadAsStringAsync(ct);
        var doc  = new HtmlDocument();
        doc.LoadHtml(html);

        var title = HtmlEntity.DeEntitize(
            doc.DocumentNode.SelectSingleNode("//h1")?.InnerText.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(title)) return null;

        if (IsNonFood(title))
        {
            Console.WriteLine($"[{SiteName}][Skip] non-food: {title}");
            return null;
        }

        // ── Price ──────────────────────────────────────────────────────────────
        decimal? price = ParsePrice(doc);

        // ── Image ──────────────────────────────────────────────────────────────
        string? imageUrl = null;
        var imgNodes = doc.DocumentNode.SelectNodes("//img[contains(@src,'/media/catalog/product/')]");
        if (imgNodes != null)
            foreach (var img in imgNodes)
            {
                var src = img.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(src))
                {
                    imageUrl = src.StartsWith("//") ? "https:" + src : src;
                    // strip query string for a clean URL
                    var qi = imageUrl.IndexOf('?');
                    if (qi > 0) imageUrl = imageUrl[..qi];
                    break;
                }
            }

        // ── Ingredients & nutrition ────────────────────────────────────────────
        var (ingredientsText, nutritionText) = ParseDescriptions(doc);

        // ── Nutrients ─────────────────────────────────────────────────────────
        var src2       = !string.IsNullOrWhiteSpace(nutritionText) ? nutritionText : ingredientsText;
        var proteinPct  = ParseNutrientPct(src2, "蛋白質", "粗蛋白");
        var fatPct      = ParseNutrientPct(src2, "脂肪", "粗脂肪");
        var fiberPct    = ParseNutrientPct(src2, "粗纖維");
        var moisturePct = ParseNutrientPct(src2, "水分");
        var ashPct      = ParseNutrientPct(src2, "灰分", "灰份");

        double? carbsPct = null;
        if (proteinPct.HasValue && fatPct.HasValue && fiberPct.HasValue &&
            moisturePct.HasValue && ashPct.HasValue)
        {
            var c = 100 - proteinPct.Value - fatPct.Value - fiberPct.Value
                       - moisturePct.Value - ashPct.Value;
            carbsPct = Math.Round(Math.Max(c, 0), 2);
        }

        var phosphorusPct = ParseNutrientPct(src2, "磷");

        return new Product
        {
            Url               = url,
            Title             = title,
            Brand             = ExtractBrand(doc, title),
            PetType           = DetectPetType(title),
            Age               = DetectAgeStage(title),
            IsPrescription    = title.Contains("處方"),
            Form              = !string.IsNullOrEmpty(formHint) ? formHint : DetectFormFromTitle(title),
            ImageUrl          = imageUrl,
            IngredientsText   = ingredientsText,
            NutritionText     = nutritionText,
            Ingredients       = new List<Ingredient>(),
            Sections          = new Dictionary<string, string>(),
            CaloriesKcalPerKg = null,
            ProteinPct        = proteinPct,
            FatPct            = fatPct,
            FiberPct          = fiberPct,
            MoisturePct       = moisturePct,
            AshPct            = ashPct,
            CarbsPct          = carbsPct,
            PhosphorusPct     = phosphorusPct,
            Volume            = ParseVolume(title),
            Price             = price,
        };
    }

    private static decimal? ParsePrice(HtmlDocument doc)
    {
        // Prefer special-price; fall back to any price-wrapper with data-price-amount
        var node = doc.DocumentNode.SelectSingleNode(
                       "//span[contains(@class,'special-price')]//span[@data-price-amount]")
                ?? doc.DocumentNode.SelectSingleNode("//span[@data-price-amount]");
        if (node != null)
        {
            var raw = node.GetAttributeValue("data-price-amount", "");
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                return p;
        }
        return null;
    }

    // Petpark puts both ingredients and nutrition in one <p class="text-muted"> inside
    // <div class="product-info-ec-spec">, separated by <br/>
    private static (string Ingredients, string Nutrition) ParseDescriptions(HtmlDocument doc)
    {
        var ingredients = "";
        var nutrition   = "";

        var p = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class,'product-info-ec-spec')]//p");
        if (p == null) return (ingredients, nutrition);

        // Replace <br> with newline so we can split on lines
        var brNodes = p.SelectNodes(".//br");
        foreach (var br in brNodes != null ? brNodes.ToList() : new List<HtmlNode>())
            br.ParentNode.ReplaceChild(HtmlNode.CreateNode("\n"), br);

        foreach (var line in p.InnerText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = HtmlEntity.DeEntitize(line.Trim());
            if (string.IsNullOrEmpty(text)) continue;

            if ((text.StartsWith("成分") || text.StartsWith("原料")) && string.IsNullOrEmpty(ingredients))
                ingredients = Regex.Replace(text, @"^[^:：]+[:：]\s*", "");
            else if ((text.StartsWith("營養") || text.StartsWith("保證分析")) && string.IsNullOrEmpty(nutrition))
                nutrition = Regex.Replace(text, @"^[^:：]+[:：]\s*", "");
        }

        return (ingredients, nutrition);
    }

    private static string ExtractBrand(HtmlDocument doc, string title)
    {
        // Petpark puts brand in <div class="product-info-brand"><h4>...</h4></div>
        var brandNode = doc.DocumentNode.SelectSingleNode(
            "//div[contains(@class,'product-info-brand')]/h4");
        if (brandNode != null)
        {
            var brand = HtmlEntity.DeEntitize(brandNode.InnerText.Trim());
            if (!string.IsNullOrEmpty(brand)) return brand;
        }

        return "";
    }

    // ── Detection helpers ─────────────────────────────────────────────────────

    private static readonly string[] NonFoodTitleKeywords =
        ["貓薄荷", "貓草", "木天蓼", "逗貓棒", "貓砂"];

    private static bool IsNonFood(string title)
        => NonFoodTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string DetectPetType(string title)
    {
        if (title.Contains("犬") || title.Contains("狗")) return "dog";
        if (title.Contains("貓"))                         return "cat";
        return "";
    }

    private static string DetectAgeStage(string title)
    {
        if (title.Contains("幼犬") || title.Contains("幼貓") || title.Contains("幼齡") || title.Contains("成長期"))
            return "puppy";
        if (title.Contains("老犬") || title.Contains("老貓") || title.Contains("高齡") || title.Contains("熟齡"))
            return "senior";
        return "adult";
    }

    private static string DetectFormFromTitle(string title)
    {
        if (title.Contains("乾糧") || title.Contains("飼料")) return "dry";
        if (title.Contains("罐頭") || title.Contains("主食罐")) return "can";
        if (title.Contains("餐包") || title.Contains("濕食")) return "wet";
        if (title.Contains("零食") || title.Contains("點心") || title.Contains("肉乾")) return "treat";
        return "";
    }

    private static double? ParseNutrientPct(string text, params string[] names)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var name in names)
        {
            var idx = text.IndexOf(name, StringComparison.Ordinal);
            if (idx < 0) continue;
            var match = Regex.Match(text[(idx + name.Length)..], @"(\d+\.?\d*)[%％]");
            if (match.Success && double.TryParse(match.Groups[1].Value,
                NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                return val;
        }
        return null;
    }

    private static string? ParseVolume(string title)
    {
        var m = Regex.Match(title, @"(\d+(?:\.\d+)?)\s*(kg|g|ml|mL|L|lb|lbs|公克|克)\b",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : null;
    }
}
