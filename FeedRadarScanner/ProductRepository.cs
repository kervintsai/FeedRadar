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

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Products (
                Id              SERIAL PRIMARY KEY,
                Url             TEXT UNIQUE NOT NULL,
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

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS FilterOptions (
                Category TEXT NOT NULL,
                Value    TEXT NOT NULL,
                Label    TEXT NOT NULL,
                Count    INT  NOT NULL,
                PRIMARY KEY (Category, Value)
            );
            """);

        foreach (var (col, def) in new[]
        {
            ("Brand",          "TEXT NOT NULL DEFAULT ''"),
            ("PetType",        "TEXT NOT NULL DEFAULT ''"),
            ("AgeStage",       "TEXT NOT NULL DEFAULT ''"),
            ("IsPrescription", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("Form",           "TEXT NOT NULL DEFAULT ''"),
            ("ImageUrl",       "TEXT"),
            ("NutritionText",  "TEXT NOT NULL DEFAULT ''"),
            ("CaloriesText",   "TEXT"),
            ("ProteinPct",     "DOUBLE PRECISION"),
            ("FatPct",         "DOUBLE PRECISION"),
            ("FiberPct",       "DOUBLE PRECISION"),
        })
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");
    }

    public void Upsert(Product product)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (
                Url, Title, Brand, PetType, AgeStage, IsPrescription, Form,
                ImageUrl, IngredientsText, NutritionText, CaloriesText,
                ProteinPct, FatPct, FiberPct, ScannedAt
            )
            VALUES (
                @url, @title, @brand, @petType, @ageStage, @isPrescription, @form,
                @imageUrl, @ingredients, @nutrition, @calories,
                @protein, @fat, @fiber, @scannedAt
            )
            ON CONFLICT (Url) DO UPDATE SET
                Title           = EXCLUDED.Title,
                Brand           = EXCLUDED.Brand,
                PetType         = EXCLUDED.PetType,
                AgeStage        = EXCLUDED.AgeStage,
                IsPrescription  = EXCLUDED.IsPrescription,
                Form            = EXCLUDED.Form,
                ImageUrl        = EXCLUDED.ImageUrl,
                IngredientsText = EXCLUDED.IngredientsText,
                NutritionText   = EXCLUDED.NutritionText,
                CaloriesText    = EXCLUDED.CaloriesText,
                ProteinPct      = EXCLUDED.ProteinPct,
                FatPct          = EXCLUDED.FatPct,
                FiberPct        = EXCLUDED.FiberPct,
                ScannedAt       = EXCLUDED.ScannedAt;
            """;
        cmd.Parameters.AddWithValue("url",            product.Url);
        cmd.Parameters.AddWithValue("title",          product.Title);
        cmd.Parameters.AddWithValue("brand",          product.Brand);
        cmd.Parameters.AddWithValue("petType",        product.PetType);
        cmd.Parameters.AddWithValue("ageStage",       product.AgeStage);
        cmd.Parameters.AddWithValue("isPrescription", product.IsPrescription);
        cmd.Parameters.AddWithValue("form",           product.Form);
        cmd.Parameters.AddWithValue("imageUrl",       product.ImageUrl as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ingredients",    product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition",      product.NutritionText);
        cmd.Parameters.AddWithValue("calories",       product.CaloriesText as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("protein",        product.ProteinPct   as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat",            product.FatPct       as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber",          product.FiberPct     as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scannedAt",      DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

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
        Exec(conn, "TRUNCATE Products RESTART IDENTITY;");
        // FilterOptions is intentionally NOT truncated here — keep stale filter data
        // available to the web server during the re-scan window. RebuildFilters() at
        // the end of the scan will overwrite it with fresh data.
        Console.WriteLine("[Cleanup] Products truncated (FilterOptions preserved until RebuildFilters).");
    }

    private static readonly string[] CommonMeats =
        ["雞肉", "牛肉", "鮭魚", "鮪魚", "鴨肉", "羊肉", "豬肉", "火雞", "鹿肉", "兔肉", "鯖魚", "鱈魚", "鯛魚"];

    public void RebuildFilters()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        var rows = new List<(string Category, string Value, string Label, int Count)>();

        // Brands
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Brand, COUNT(*)::int FROM Products WHERE Brand != '' GROUP BY Brand ORDER BY COUNT(*) DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(("brand", r.GetString(0), r.GetString(0), r.GetInt32(1)));
        }

        // Ingredients (common meats)
        foreach (var meat in CommonMeats)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE IngredientsText ILIKE @kw;";
            cmd.Parameters.AddWithValue("kw", $"%{meat}%");
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("ingredient", meat, meat, count));
        }

        // AgeStages
        foreach (var (value, label) in new[] { ("puppy", "幼齡"), ("senior", "高齡") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE AgeStage = @v;";
            cmd.Parameters.AddWithValue("v", value);
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("ageStage", value, label, count));
        }

        // PetTypes
        foreach (var (value, label) in new[] { ("cat", "貓"), ("dog", "狗") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE PetType = @v;";
            cmd.Parameters.AddWithValue("v", value);
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("petType", value, label, count));
        }

        // Forms
        foreach (var (value, label) in new[] { ("wet", "濕食"), ("dry", "乾糧") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE Form = @v;";
            cmd.Parameters.AddWithValue("v", value);
            var count = (int)(cmd.ExecuteScalar() ?? 0);
            if (count > 0) rows.Add(("form", value, label, count));
        }

        // IsPrescription
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
