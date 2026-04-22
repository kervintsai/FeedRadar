using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("FeedScraper starting...");

        var collectionUrl = args.Length > 0 ? args[0] : "https://www.lovecat.com.tw/collections/%E7%8A%AC%E4%B9%BE%E7%B3%A7%E4%B8%BB%E9%A3%9F_%E5%85%A8%E9%83%A8%E5%95%86%E5%93%81-20210721151014";
        var productUrls = await GetProductUrlsFromCollection(collectionUrl);
        Console.WriteLine($"Found {productUrls.Count} product urls");

        EnsureDatabase();

        foreach (var url in productUrls)
        {
            try
            {
                var product = await ScrapeProduct(url);
                SaveProductToDb(product);
                Console.WriteLine($"Saved: {product.Title}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scraping {url}: {ex.Message}");
            }
        }

        Console.WriteLine("Done");
    }

    static async Task<List<string>> GetProductUrlsFromCollection(string collectionUrl)
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(collectionUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var links = new List<string>();
        // product links are in anchor tags under .product-card or collection grid
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null))
        {
            var href = a.GetAttributeValue("href", "");
            if (href.Contains("/products/"))
            {
                var full = href.StartsWith("http") ? href : new Uri(new Uri(collectionUrl), href).ToString();
                if (!links.Contains(full)) links.Add(full);
            }
        }

        return links;
    }

    static async Task<Product> ScrapeProduct(string url)
    {
        using var http = new HttpClient();
        var html = await http.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText?.Trim() ?? "";

        // find ingredient / 成份 related nodes - look for keywords
        var ingredientNodes = new List<HtmlNode>();
        var keywords = new[] { "成份", "配料", "Ingredients", "主要成份" };
        foreach (var k in keywords)
        {
            var node = doc.DocumentNode.SelectSingleNode($"//*[contains(text(), '{k}')]");
            if (node != null) ingredientNodes.Add(node);
        }

        // fallback: look for elements with product description
        if (ingredientNodes.Count == 0)
        {
            var desc = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'product-single__description')]")
                    ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'description')]");

            if (desc != null) ingredientNodes.Add(desc);
        }

        var ingredients = new List<string>();
        foreach (var node in ingredientNodes)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
            // split by common separators
            var parts = text.Split(new[] { '\n', ',', '、', ';', '，', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0 && t.Length < 300)
                {
                    // ignore headings
                    if (t.Contains("成份") || t.Contains("Ingredients") || t.Contains("配料") || t.Contains("主要成份")) continue;
                    ingredients.Add(t);
                }
            }
        }

        // dedupe while preserving order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedup = new List<string>();
        foreach (var s in ingredients)
        {
            if (seen.Add(s)) dedup.Add(s);
        }

        // limit to 20 ingredient columns
        while (dedup.Count < 20) dedup.Add("");
        if (dedup.Count > 20) dedup = dedup.GetRange(0, 20);

        return new Product { Url = url, Title = title, Ingredients = dedup };
    }

    static void EnsureDatabase()
    {
        using var conn = new SqliteConnection("Data Source=feeds.db");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Products (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Url TEXT UNIQUE,
    Title TEXT,
    Ingredient1 TEXT,
    Ingredient2 TEXT,
    Ingredient3 TEXT,
    Ingredient4 TEXT,
    Ingredient5 TEXT,
    Ingredient6 TEXT,
    Ingredient7 TEXT,
    Ingredient8 TEXT,
    Ingredient9 TEXT,
    Ingredient10 TEXT,
    Ingredient11 TEXT,
    Ingredient12 TEXT,
    Ingredient13 TEXT,
    Ingredient14 TEXT,
    Ingredient15 TEXT,
    Ingredient16 TEXT,
    Ingredient17 TEXT,
    Ingredient18 TEXT,
    Ingredient19 TEXT,
    Ingredient20 TEXT
);""";
        cmd.ExecuteNonQuery();
    }

    static void SaveProductToDb(Product p)
    {
        using var conn = new SqliteConnection("Data Source=feeds.db");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO Products (Url, Title, Ingredient1, Ingredient2, Ingredient3, Ingredient4, Ingredient5, Ingredient6, Ingredient7, Ingredient8, Ingredient9, Ingredient10, Ingredient11, Ingredient12, Ingredient13, Ingredient14, Ingredient15, Ingredient16, Ingredient17, Ingredient18, Ingredient19, Ingredient20)
VALUES ($url, $title, $i1, $i2, $i3, $i4, $i5, $i6, $i7, $i8, $i9, $i10, $i11, $i12, $i13, $i14, $i15, $i16, $i17, $i18, $i19, $i20);";

        cmd.Parameters.AddWithValue("$url", p.Url);
        cmd.Parameters.AddWithValue("$title", p.Title ?? "");
        for (int i = 0; i < 20; i++)
        {
            cmd.Parameters.AddWithValue("$i" + (i + 1), p.Ingredients.Count > i ? p.Ingredients[i] : "");
        }

        cmd.ExecuteNonQuery();
    }
}

class Product
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Ingredients { get; set; } = new List<string>();
}
