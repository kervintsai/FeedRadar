using Npgsql;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(string connectionString)
    {
        _connectionString = ParseConnectionString(connectionString);
        EnsureSchema();
    }

    private static string ParseConnectionString(string s)
    {
        if (!s.StartsWith("postgres://") && !s.StartsWith("postgresql://"))
            return s;
        var uri   = new Uri(s);
        var parts = uri.UserInfo.Split(':', 2);
        var user  = Uri.UnescapeDataString(parts[0]);
        var pass  = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        var db    = uri.AbsolutePath.TrimStart('/');
        return $"Host={uri.Host};Port={uri.Port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
    }

    private void EnsureSchema()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        Exec(conn, "CREATE EXTENSION IF NOT EXISTS pg_trgm;");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Products (
                Id              SERIAL PRIMARY KEY,
                Url             TEXT NOT NULL,
                Title           TEXT NOT NULL DEFAULT '',
                Brand           TEXT NOT NULL DEFAULT '',
                PetType         TEXT NOT NULL DEFAULT '',
                IsPrescription  BOOLEAN NOT NULL DEFAULT FALSE,
                Form            TEXT NOT NULL DEFAULT '',
                IngredientsText TEXT NOT NULL DEFAULT '',
                NutritionText   TEXT NOT NULL DEFAULT '',
                CaloriesText    TEXT,
                ProteinPct      DOUBLE PRECISION,
                FatPct          DOUBLE PRECISION,
                FiberPct        DOUBLE PRECISION,
                ScannedAt       TEXT NOT NULL DEFAULT ''
            );
            """);

        // Remove legacy Url unique constraint so Products can aggregate across sites
        Exec(conn, "ALTER TABLE Products DROP CONSTRAINT IF EXISTS products_url_key;");

        foreach (var (col, def) in new[]
        {
            ("AgeStage",       "TEXT NOT NULL DEFAULT ''"),
            ("ImageUrl",       "TEXT"),
            ("NutritionText",  "TEXT NOT NULL DEFAULT ''"),
            ("CaloriesText",   "TEXT"),
            ("ProteinPct",     "DOUBLE PRECISION"),
            ("FatPct",         "DOUBLE PRECISION"),
            ("FiberPct",       "DOUBLE PRECISION"),
            ("MoisturePct",    "DOUBLE PRECISION"),
            ("AshPct",         "DOUBLE PRECISION"),
            ("CarbsPct",       "DOUBLE PRECISION"),
            ("MinPrice",       "NUMERIC"),
            ("MaxPrice",       "NUMERIC"),
        })
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");

        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_products_brand ON Products(Brand);");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_products_title_trgm ON Products USING gin(Title gin_trgm_ops);");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductPrices (
                Id        SERIAL PRIMARY KEY,
                ProductId INT     NOT NULL REFERENCES Products(Id),
                Site      TEXT    NOT NULL,
                Price     NUMERIC NOT NULL,
                Currency  TEXT    NOT NULL DEFAULT 'TWD',
                Url       TEXT    NOT NULL,
                ScannedAt TEXT    NOT NULL,
                UNIQUE (ProductId, Site)
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS FilterOptions (
                Category TEXT NOT NULL,
                Value    TEXT NOT NULL,
                Label    TEXT NOT NULL,
                Count    INT  NOT NULL,
                PRIMARY KEY (Category, Value)
            );
            """);
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    public void Upsert(Product product, string site)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        var productId = FindOrCreateProduct(conn, product);
        MergeProductData(conn, productId, product);

        if (product.Price.HasValue)
        {
            UpsertPrice(conn, productId, site, product.Price.Value, product.Url);
            UpdatePriceRange(conn, productId);
        }
    }

    private static int FindOrCreateProduct(NpgsqlConnection conn, Product product)
    {
        using var findCmd = conn.CreateCommand();
        if (!string.IsNullOrWhiteSpace(product.Brand))
        {
            findCmd.CommandText = """
                SELECT Id FROM Products
                WHERE similarity(Brand, @brand) > 0.5
                  AND similarity(Title, @title) > 0.4
                ORDER BY similarity(Brand, @brand) + similarity(Title, @title) DESC
                LIMIT 1;
                """;
            findCmd.Parameters.AddWithValue("brand", product.Brand);
            findCmd.Parameters.AddWithValue("title", product.Title);
        }
        else
        {
            findCmd.CommandText = """
                SELECT Id FROM Products
                WHERE similarity(Title, @title) > 0.6
                ORDER BY similarity(Title, @title) DESC
                LIMIT 1;
                """;
            findCmd.Parameters.AddWithValue("title", product.Title);
        }

        var result = findCmd.ExecuteScalar();
        if (result != null && result != DBNull.Value)
            return Convert.ToInt32(result);

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO Products (
                Url, Title, Brand, PetType, AgeStage, IsPrescription, Form,
                ImageUrl, IngredientsText, NutritionText, CaloriesText,
                ProteinPct, FatPct, FiberPct, MoisturePct, AshPct, CarbsPct, ScannedAt
            ) VALUES (
                @url, @title, @brand, @petType, @ageStage, @isPrescription, @form,
                @imageUrl, @ingredients, @nutrition, @calories,
                @protein, @fat, @fiber, @moisture, @ash, @carbs, @scannedAt
            )
            RETURNING Id;
            """;
        SetProductParams(insertCmd, product);
        return Convert.ToInt32(insertCmd.ExecuteScalar());
    }

    private static void MergeProductData(NpgsqlConnection conn, int productId, Product product)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Products SET
                ImageUrl        = COALESCE(ImageUrl,        @imageUrl),
                IngredientsText = CASE WHEN IngredientsText = '' THEN @ingredients ELSE IngredientsText END,
                NutritionText   = CASE WHEN NutritionText   = '' THEN @nutrition   ELSE NutritionText   END,
                CaloriesText    = COALESCE(CaloriesText,    @calories),
                ProteinPct      = COALESCE(ProteinPct,      @protein),
                FatPct          = COALESCE(FatPct,          @fat),
                FiberPct        = COALESCE(FiberPct,        @fiber),
                MoisturePct     = COALESCE(MoisturePct,     @moisture),
                AshPct          = COALESCE(AshPct,          @ash),
                CarbsPct        = COALESCE(CarbsPct,        @carbs),
                Form            = CASE WHEN Form    = '' THEN @form    ELSE Form    END,
                PetType         = CASE WHEN PetType = '' THEN @petType ELSE PetType END,
                AgeStage        = CASE WHEN AgeStage= '' THEN @ageStage ELSE AgeStage END,
                ScannedAt       = @scannedAt
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("id",          productId);
        cmd.Parameters.AddWithValue("imageUrl",    product.ImageUrl      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ingredients", product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition",   product.NutritionText);
        cmd.Parameters.AddWithValue("calories",    product.CaloriesText  as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("protein",     product.ProteinPct    as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat",         product.FatPct        as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber",       product.FiberPct      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("moisture",    product.MoisturePct   as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ash",         product.AshPct        as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("carbs",       product.CarbsPct      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("form",        product.Form);
        cmd.Parameters.AddWithValue("petType",     product.PetType);
        cmd.Parameters.AddWithValue("ageStage",    product.AgeStage);
        cmd.Parameters.AddWithValue("scannedAt",   DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertPrice(NpgsqlConnection conn, int productId, string site, decimal price, string url)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ProductPrices (ProductId, Site, Price, Url, ScannedAt)
            VALUES (@productId, @site, @price, @url, @scannedAt)
            ON CONFLICT (ProductId, Site) DO UPDATE SET
                Price     = EXCLUDED.Price,
                Url       = EXCLUDED.Url,
                ScannedAt = EXCLUDED.ScannedAt;
            """;
        cmd.Parameters.AddWithValue("productId", productId);
        cmd.Parameters.AddWithValue("site",      site);
        cmd.Parameters.AddWithValue("price",     price);
        cmd.Parameters.AddWithValue("url",       url);
        cmd.Parameters.AddWithValue("scannedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void UpdatePriceRange(NpgsqlConnection conn, int productId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Products SET
                MinPrice = (SELECT MIN(Price) FROM ProductPrices WHERE ProductId = @id),
                MaxPrice = (SELECT MAX(Price) FROM ProductPrices WHERE ProductId = @id)
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("id", productId);
        cmd.ExecuteNonQuery();
    }

    private static void SetProductParams(NpgsqlCommand cmd, Product product)
    {
        cmd.Parameters.AddWithValue("url",            product.Url);
        cmd.Parameters.AddWithValue("title",          product.Title);
        cmd.Parameters.AddWithValue("brand",          product.Brand);
        cmd.Parameters.AddWithValue("petType",        product.PetType);
        cmd.Parameters.AddWithValue("ageStage",       product.AgeStage);
        cmd.Parameters.AddWithValue("isPrescription", product.IsPrescription);
        cmd.Parameters.AddWithValue("form",           product.Form);
        cmd.Parameters.AddWithValue("imageUrl",       product.ImageUrl      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ingredients",    product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition",      product.NutritionText);
        cmd.Parameters.AddWithValue("calories",       product.CaloriesText  as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("protein",        product.ProteinPct    as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat",            product.FatPct        as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber",          product.FiberPct      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("moisture",       product.MoisturePct   as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ash",            product.AshPct        as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("carbs",          product.CarbsPct      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scannedAt",      DateTime.UtcNow.ToString("O"));
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    public int GetProductCount()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Products;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void TruncateAll()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        // ProductPrices must be cleared first due to FK; CASCADE handles it
        Exec(conn, "TRUNCATE Products RESTART IDENTITY CASCADE;");
        Console.WriteLine("[Cleanup] Products + ProductPrices truncated (FilterOptions preserved).");
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private static readonly string[] CommonMeats =
        ["雞肉", "牛肉", "鮭魚", "鮪魚", "鴨肉", "羊肉", "豬肉", "火雞", "鹿肉", "兔肉", "鯖魚", "鱈魚", "鯛魚"];

    public void RebuildFilters()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        var rows = new List<(string Category, string Value, string Label, int Count)>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Brand, COUNT(*)::int FROM Products WHERE Brand != '' GROUP BY Brand ORDER BY COUNT(*) DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(("brand", r.GetString(0), r.GetString(0), r.GetInt32(1)));
        }

        foreach (var meat in CommonMeats)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE IngredientsText ILIKE @kw;";
            cmd.Parameters.AddWithValue("kw", $"%{meat}%");
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("ingredient", meat, meat, count));
        }

        foreach (var (value, label) in new[] { ("puppy", "幼齡"), ("adult", "成年"), ("senior", "高齡") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE AgeStage = @v;";
            cmd.Parameters.AddWithValue("v", value);
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("ageStage", value, label, count));
        }

        foreach (var (value, label) in new[] { ("cat", "貓"), ("dog", "狗") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE PetType = @v;";
            cmd.Parameters.AddWithValue("v", value);
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("petType", value, label, count));
        }

        foreach (var (value, label) in new[] { ("dry", "乾糧"), ("wet", "濕食"), ("can", "罐頭"), ("treat", "零食") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE Form = @v;";
            cmd.Parameters.AddWithValue("v", value);
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("form", value, label, count));
        }

        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE IsPrescription = TRUE;";
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("isPrescription", "true", "處方飼料", count));
        }

        Exec(conn, "TRUNCATE FilterOptions;");
        foreach (var (category, value, label, count) in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO FilterOptions (Category, Value, Label, Count) VALUES (@cat, @val, @lbl, @cnt);";
            cmd.Parameters.AddWithValue("cat", category);
            cmd.Parameters.AddWithValue("val", value);
            cmd.Parameters.AddWithValue("lbl", label);
            cmd.Parameters.AddWithValue("cnt", count);
            cmd.ExecuteNonQuery();
        }

        Console.WriteLine($"[Filters] Rebuilt {rows.Count} filter options.");
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
