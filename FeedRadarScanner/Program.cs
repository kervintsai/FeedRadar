// 從執行檔位置往上找 solution 根目錄的 feeds.db
// 不管從 VS 還是 dotnet run 都能找到正確位置
string DefaultDbPath()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 5; i++)
    {
        var candidate = Path.Combine(dir, "feeds.db");
        // 找到 .git 或 .slnx 就是 solution 根目錄
        if (Directory.GetFiles(dir, "*.slnx").Length > 0 ||
            Directory.Exists(Path.Combine(dir, ".git")))
            return candidate;
        dir = Path.GetFullPath(Path.Combine(dir, ".."));
    }
    return "feeds.db";
}

var dbPath = args.Length > 0 ? args[0] : DefaultDbPath();
Console.WriteLine($"DB path: {Path.GetFullPath(dbPath)}");

var scanner = new LovecatScanner();
var repo = new ProductRepository(dbPath);

var products = await scanner.ScanAsync(
    "https://www.lovecat.com.tw/collections/%E7%8A%AC%E4%B9%BE%E7%B3%A7%E4%B8%BB%E9%A3%9F_%E5%85%A8%E9%83%A8%E5%95%86%E5%93%81-20210721151014"
);

Console.WriteLine($"Saving {products.Count} products to {dbPath}...");
foreach (var p in products)
    repo.Upsert(p);

Console.WriteLine("Done.");
