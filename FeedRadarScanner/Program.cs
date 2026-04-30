var connectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? (args.Length > 0 ? args[0] : "Host=localhost;Database=feedradar;Username=postgres;Password=postgres");

Console.WriteLine("Connecting to database...");
var repo = new ProductRepository(connectionString);

var scannerCollections = new (IFeedScanner Scanner, string[] Collections)[]
{
    (new LovecatScanner(), [
        "https://www.lovecat.com.tw/collections/犬乾糧主食_全部商品-20210721151014",
        "https://www.lovecat.com.tw/collections/犬罐頭-餐包_全部商品",
        "https://www.lovecat.com.tw/collections/犬點心零食_全部商品-20210721142445",
        "https://www.lovecat.com.tw/collections/貓-乾糧主食_全部商品-20210721141926",
        "https://www.lovecat.com.tw/collections/貓-罐頭-餐包_全部商品",
        "https://www.lovecat.com.tw/collections/貓零食點心_全部商品",
        "https://www.lovecat.com.tw/collections/處方乾糧罐頭_全部商品",
        "https://www.lovecat.com.tw/collections/冷凍生鮮食專區_全部商品",
    ]),
    (new PetparkScanner(), [
        // Dry food
        "https://shop.petpark.com.tw/dryfood/hot-cat-dryfood",
        "https://shop.petpark.com.tw/dryfood/hot-dog-dryfood",
        "https://shop.petpark.com.tw/dryfood/prescription-dryfood-cat",
        "https://shop.petpark.com.tw/dryfood/prescription-dryfood-dog",
        // Canned (main course)
        "https://shop.petpark.com.tw/cans/can-maincourse-cat",
        "https://shop.petpark.com.tw/cans/can-maincourse-dog",
        // Wet (pouches)
        "https://shop.petpark.com.tw/cans/main-pouchfood-cat",
        "https://shop.petpark.com.tw/cans/main-pouchfood-dog",
        // Treats
        "https://shop.petpark.com.tw/treats/treats-cat/treats-muddysnack-cat",
        "https://shop.petpark.com.tw/treats/treats-cat/natural-freezedried-cat",
        "https://shop.petpark.com.tw/treats/treats-cat/treats-driedfish-cat",
        "https://shop.petpark.com.tw/treats/treats-cat/fishsticks-cat",
        "https://shop.petpark.com.tw/treats/treats-dog/treats-jerky-dog",
        "https://shop.petpark.com.tw/treats/treats-dog/natural-freezedried-dog",
        "https://shop.petpark.com.tw/treats/treats-dog/functional-snack-dog",
    ]),
};

var quickScan = Environment.GetEnvironmentVariable("QUICK_SCAN") == "true";
Console.WriteLine(quickScan ? "[Mode] QUICK_SCAN" : "[Mode] FULL_SCAN");

repo.TruncateAll();

int total = 0;
foreach (var (scanner, allCollections) in scannerCollections)
{
    var collections = quickScan ? allCollections.Take(1).ToArray() : allCollections;
    foreach (var url in collections)
    {
        Console.WriteLine($"\n========================================");
        Console.WriteLine($"[Collection] {url}");
        try
        {
            var products = await scanner.ScanAsync(url);
            Console.WriteLine($"[Collection] Fetched {products.Count} products, saving...");
            foreach (var p in products)
                repo.Upsert(p, scanner.SiteName);
            total += products.Count;
            Console.WriteLine($"[Collection] Saved OK. Running total: {total}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERR] Collection failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}

Console.WriteLine($"\n========================================");
Console.WriteLine($"Done. Total saved: {total}");
Console.WriteLine($"DB product count: {repo.GetProductCount()}");

repo.RebuildFilters();
