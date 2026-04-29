// 連線字串優先讀 DATABASE_URL（Railway），其次用命令列引數，最後 fallback 到本地 dev
var connectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? (args.Length > 0 ? args[0] : "Host=localhost;Database=feedradar;Username=postgres;Password=postgres");

Console.WriteLine("Connecting to database...");

var scanner = new LovecatScanner();
var repo    = new ProductRepository(connectionString);

repo.TruncateAll();

string[] allCollections =
[
    "https://www.lovecat.com.tw/collections/犬乾糧主食_全部商品-20210721151014",
    "https://www.lovecat.com.tw/collections/犬罐頭-餐包_全部商品",
    "https://www.lovecat.com.tw/collections/犬點心零食_全部商品-20210721142445",
    "https://www.lovecat.com.tw/collections/貓-乾糧主食_全部商品-20210721141926",
    "https://www.lovecat.com.tw/collections/貓-罐頭-餐包_全部商品",
    "https://www.lovecat.com.tw/collections/貓零食點心_全部商品",
    "https://www.lovecat.com.tw/collections/處方乾糧罐頭_全部商品",
    "https://www.lovecat.com.tw/collections/冷凍生鮮食專區_全部商品",
];

// QUICK_SCAN=true → only scan one dog + one cat collection for fast testing
string[] quickCollections =
[
    "https://www.lovecat.com.tw/collections/犬點心零食_全部商品-20210721142445",
    "https://www.lovecat.com.tw/collections/貓零食點心_全部商品",
];

var quickScan = Environment.GetEnvironmentVariable("QUICK_SCAN") == "true";
var collections = quickScan ? quickCollections : allCollections;
Console.WriteLine(quickScan ? "[Mode] QUICK_SCAN — 2 collections only" : "[Mode] FULL_SCAN — all collections");

int total = 0;
foreach (var url in collections)
{
    Console.WriteLine($"\n========================================");
    Console.WriteLine($"[Collection] {url}");
    try
    {
        var products = await scanner.ScanAsync(url);
        Console.WriteLine($"[Collection] Fetched {products.Count} products, saving...");
        foreach (var p in products)
            repo.Upsert(p);
        total += products.Count;
        Console.WriteLine($"[Collection] Saved OK. Running total: {total}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERR] Collection failed: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}

Console.WriteLine($"\n========================================");
Console.WriteLine($"Done. Total saved: {total}");
Console.WriteLine($"DB product count: {repo.GetProductCount()}");

repo.RebuildFilters();
