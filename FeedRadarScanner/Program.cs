// 連線字串優先讀 DATABASE_URL（Railway），其次用命令列引數，最後 fallback 到本地 dev
var connectionString =
    Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? (args.Length > 0 ? args[0] : "Host=localhost;Database=feedradar;Username=postgres;Password=postgres");

Console.WriteLine($"Connecting to database...");

var scanner = new LovecatScanner();
var repo = new ProductRepository(connectionString);

var products = await scanner.ScanAsync(
    "https://www.lovecat.com.tw/collections/%E7%8A%AC%E4%B9%BE%E7%B3%A7%E4%B8%BB%E9%A3%9F_%E5%85%A8%E9%83%A8%E5%95%86%E5%93%81-20210721151014"
);

Console.WriteLine($"Saving {products.Count} products...");
foreach (var p in products)
    repo.Upsert(p);

repo.DeleteStale(products.Select(p => p.Url));
Console.WriteLine("Done.");
