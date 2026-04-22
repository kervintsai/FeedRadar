var dbPath = args.Length > 0 ? args[0] : "../feeds.db";
var scanner = new LovecatScanner();
var repo = new ProductRepository(dbPath);

var products = await scanner.ScanAsync(
    "https://www.lovecat.com.tw/collections/%E7%8A%AC%E4%B9%BE%E7%B3%A7%E4%B8%BB%E9%A3%9F_%E5%85%A8%E9%83%A8%E5%95%86%E5%93%81-20210721151014"
);

Console.WriteLine($"Saving {products.Count} products to {dbPath}...");
foreach (var p in products)
    repo.Upsert(p);

Console.WriteLine("Done.");
