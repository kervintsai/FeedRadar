using Npgsql;

public class ProductRepository
{
    private readonly string _connectionString;

    private static readonly string[] CommonMeats =
    [
        "雞肉", "牛肉", "鮭魚", "鮪魚", "鴨肉",
        "羊肉", "豬肉", "火雞", "鹿肉", "兔肉", "鯖魚", "鱈魚", "鯛魚",
    ];

    public ProductRepository(IConfiguration config)
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        var raw = !string.IsNullOrEmpty(databaseUrl)
            ? databaseUrl
            : config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("No database connection string configured.");
        _connectionString = ParseConnectionString(raw);
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

    // ── Filters ───────────────────────────────────────────────────────────────

    public FiltersDto GetFilters()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        var productCount = Count(conn, "SELECT COUNT(*) FROM Products");
        Console.WriteLine($"[GetFilters] Products={productCount}");

        var all = Query<(string Category, string Value, string Label, int Count)>(conn,
            "SELECT Category, Value, Label, Count FROM FilterOptions ORDER BY Count DESC;",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));

        Console.WriteLine($"[GetFilters] FilterOptions rows={all.Count}");

        if (all.Count == 0)
            all = BuildFiltersFromProducts(conn);

        Console.WriteLine($"[GetFilters] returning {all.Count} filter rows");

        FilterOption Map((string Category, string Value, string Label, int Count) row)
            => new(row.Value, row.Label, row.Count);

        return new FiltersDto(
            Brands:         all.Where(r => r.Category == "brand").Select(Map).ToList(),
            Ingredients:    all.Where(r => r.Category == "ingredient").Select(Map).ToList(),
            PetTypes:       all.Where(r => r.Category == "petType").Select(Map).ToList(),
            AgeStages:      all.Where(r => r.Category == "ageStage").Select(Map).ToList(),
            Forms:          all.Where(r => r.Category == "form").Select(Map).ToList(),
            IsPrescription: all.Where(r => r.Category == "isPrescription").Select(Map).ToList()
        );
    }

    private List<(string Category, string Value, string Label, int Count)> BuildFiltersFromProducts(NpgsqlConnection conn)
    {
        Console.WriteLine("[BuildFilters] building from Products table");
        var rows = new List<(string, string, string, int)>();

        try
        {
            var brands = Query<(string, string, string, int)>(conn,
                "SELECT Brand, Brand, COUNT(*)::int FROM Products WHERE Brand != '' GROUP BY Brand ORDER BY COUNT(*) DESC;",
                r => ("brand", r.GetString(0), r.GetString(1), r.GetInt32(2)));
            rows.AddRange(brands);
            Console.WriteLine($"[BuildFilters] brands={brands.Count}");
        }
        catch (Exception ex) { Console.WriteLine($"[BuildFilters] brand query failed: {ex.Message}"); }

        try
        {
            foreach (var meat in CommonMeats)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*)::int FROM Products WHERE IngredientsText ILIKE @kw;";
                cmd.Parameters.AddWithValue("kw", $"%{meat}%");
                var count = (int)(cmd.ExecuteScalar() ?? 0);
                if (count > 0) rows.Add(("ingredient", meat, meat, count));
            }
            Console.WriteLine($"[BuildFilters] ingredients added");
        }
        catch (Exception ex) { Console.WriteLine($"[BuildFilters] ingredient query failed: {ex.Message}"); }

        try
        {
            foreach (var (value, label) in new[] { ("puppy", "幼齡"), ("adult", "成年"), ("senior", "高齡") })
            {
                var c = Count(conn, $"SELECT COUNT(*) FROM Products WHERE AgeStage='{value}'");
                if (c > 0) rows.Add(("ageStage", value, label, c));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[BuildFilters] ageStage query failed: {ex.Message}"); }

        try
        {
            foreach (var (value, label) in new[] { ("cat", "貓"), ("dog", "狗") })
            {
                var c = Count(conn, $"SELECT COUNT(*) FROM Products WHERE PetType='{value}'");
                if (c > 0) rows.Add(("petType", value, label, c));
            }
            Console.WriteLine($"[BuildFilters] petTypes added");
        }
        catch (Exception ex) { Console.WriteLine($"[BuildFilters] petType query failed: {ex.Message}"); }

        try
        {
            foreach (var (value, label) in new[] { ("wet", "濕食"), ("dry", "乾糧") })
            {
                var c = Count(conn, $"SELECT COUNT(*) FROM Products WHERE Form='{value}'");
                if (c > 0) rows.Add(("form", value, label, c));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[BuildFilters] form query failed: {ex.Message}"); }

        try
        {
            var presCount = Count(conn, "SELECT COUNT(*) FROM Products WHERE IsPrescription=TRUE");
            if (presCount > 0) rows.Add(("isPrescription", "true", "處方飼料", presCount));
        }
        catch (Exception ex) { Console.WriteLine($"[BuildFilters] isPrescription query failed: {ex.Message}"); }

        Console.WriteLine($"[BuildFilters] total rows={rows.Count}");

        // Only persist if we got data — don't wipe FilterOptions with an empty result
        if (rows.Count > 0)
        {
            Exec(conn, "TRUNCATE FilterOptions;");
            foreach (var (cat, val, lbl, cnt) in rows)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO FilterOptions (Category, Value, Label, Count) VALUES (@c, @v, @l, @n);";
                cmd.Parameters.AddWithValue("c", cat);
                cmd.Parameters.AddWithValue("v", val);
                cmd.Parameters.AddWithValue("l", lbl);
                cmd.Parameters.AddWithValue("n", cnt);
                cmd.ExecuteNonQuery();
            }
        }

        return rows;
    }

    // ── Products ──────────────────────────────────────────────────────────────

    public (List<ProductDto> Products, int Total) GetAll(
        string? brand, string? ingredient, string? excludeIngredient,
        string? petType, string? ageStage, string? form, bool? isPrescription,
        int page, int limit)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        var conditions = new List<string>();
        var cmd = conn.CreateCommand();

        if (!string.IsNullOrWhiteSpace(brand))
        {
            conditions.Add("Brand = @brand");
            cmd.Parameters.AddWithValue("brand", brand);
        }

        // ingredient: multi-value OR  e.g. "雞肉,鮭魚"
        var includeList = Split(ingredient);
        if (includeList.Count > 0)
        {
            var parts = includeList.Select((v, i) =>
            {
                cmd.Parameters.AddWithValue($"ing{i}", $"%{v}%");
                return $"IngredientsText ILIKE @ing{i}";
            });
            conditions.Add("(" + string.Join(" OR ", parts) + ")");
        }

        // excludeIngredient: multi-value AND NOT  e.g. "牛肉,豬肉"
        var excludeList = Split(excludeIngredient);
        foreach (var (v, i) in excludeList.Select((v, i) => (v, i)))
        {
            cmd.Parameters.AddWithValue($"exc{i}", $"%{v}%");
            conditions.Add($"IngredientsText NOT ILIKE @exc{i}");
        }
        if (!string.IsNullOrWhiteSpace(petType))
        {
            conditions.Add("PetType = @petType");
            cmd.Parameters.AddWithValue("petType", petType);
        }
        if (!string.IsNullOrWhiteSpace(ageStage))
        {
            conditions.Add("AgeStage = @ageStage");
            cmd.Parameters.AddWithValue("ageStage", ageStage);
        }
        if (!string.IsNullOrWhiteSpace(form))
        {
            conditions.Add("Form = @form");
            cmd.Parameters.AddWithValue("form", form);
        }
        if (isPrescription.HasValue)
        {
            conditions.Add("IsPrescription = @isPrescription");
            cmd.Parameters.AddWithValue("isPrescription", isPrescription.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM Products {where}";
        foreach (NpgsqlParameter p in cmd.Parameters)
        {
            var cp = countCmd.CreateParameter();
            cp.ParameterName = p.ParameterName;
            cp.Value = p.Value;
            countCmd.Parameters.Add(cp);
        }
        var total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        cmd.CommandText = $"""
            SELECT Id, Title, Brand, PetType, AgeStage, IsPrescription, Form,
                   ImageUrl, IngredientsText, NutritionText, ProteinPct, FatPct, FiberPct, CaloriesText
            FROM Products {where}
            ORDER BY Title
            LIMIT @lim OFFSET @off;
            """;
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", (page - 1) * limit);

        var products = Query<ProductDto>(cmd, r => new ProductDto(
            Id:              r.GetInt32(0),
            Title:           r.GetString(1),
            Brand:           r.GetString(2),
            PetType:         r.GetString(3),
            AgeStage:        r.GetString(4),
            IsPrescription:  r.GetBoolean(5),
            Form:            r.GetString(6),
            ImageUrl:        r.IsDBNull(7)  ? null : r.GetString(7),
            IngredientsText: r.GetString(8),
            NutritionText:   r.GetString(9),
            ProteinPct:      r.IsDBNull(10) ? null : r.GetDouble(10),
            FatPct:          r.IsDBNull(11) ? null : r.GetDouble(11),
            FiberPct:        r.IsDBNull(12) ? null : r.GetDouble(12),
            CaloriesText:    r.IsDBNull(13) ? null : r.GetString(13)
        ));

        return (products, total);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToList();

    private static int Count(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static List<T> Query<T>(NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return ReadAll(cmd, map);
    }

    private static List<T> Query<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> map)
        => ReadAll(cmd, map);

    private static List<T> ReadAll<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> map)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
