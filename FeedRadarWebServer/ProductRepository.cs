using System.Text.Json;
using Npgsql;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(IConfiguration config)
    {
        // Railway 注入 DATABASE_URL；本地開發用 appsettings ConnectionStrings:Default
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
        var uri = new Uri(s);
        var parts = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(parts[0]);
        var pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        var db = uri.AbsolutePath.TrimStart('/');
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
                Title           TEXT NOT NULL,
                IngredientsText TEXT NOT NULL DEFAULT '',
                NutritionText   TEXT NOT NULL DEFAULT '',
                CaloriesText    TEXT,
                ProteinPct      DOUBLE PRECISION,
                FatPct          DOUBLE PRECISION,
                FiberPct        DOUBLE PRECISION,
                ScannedAt       TEXT NOT NULL
            );
            """);

        // Migration: drop old Ingredients/ProductIngredients and rebuild with Category column
        Exec(conn, """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'ingredients' AND column_name = 'basename'
                ) THEN
                    DROP TABLE IF EXISTS ProductIngredients;
                    DROP TABLE IF EXISTS Ingredients;
                END IF;
            END $$;
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Ingredients (
                Id       SERIAL PRIMARY KEY,
                Name     TEXT UNIQUE NOT NULL,
                Category TEXT NOT NULL DEFAULT ''
            );
            """);

        // Seed meat categories (idempotent via ON CONFLICT DO NOTHING)
        Exec(conn, """
            INSERT INTO Ingredients (Name, Category) VALUES
            ('雞','陸上動物'),('火雞','陸上動物'),('鴨','陸上動物'),
            ('鵝','陸上動物'),('鵪鶉','陸上動物'),('豬','陸上動物'),
            ('牛','陸上動物'),('羊','陸上動物'),('鹿','陸上動物'),
            ('袋鼠','陸上動物'),('兔','陸上動物'),('鴯鶓','陸上動物'),
            ('鮭魚','魚類'),('鱈魚','魚類'),('鯡魚','魚類'),
            ('鯖魚','魚類'),('鮪魚','魚類'),('鰹魚','魚類'),
            ('沙丁魚','魚類'),('鯷魚','魚類'),('鰈魚','魚類'),
            ('比目魚','魚類'),('鱒魚','魚類'),('鱸魚','魚類'),
            ('鰻魚','魚類'),('平鮋','魚類'),('虱目魚','魚類'),
            ('磷蝦','海鮮'),('貽貝','海鮮'),('干貝','海鮮'),
            ('龍蝦','海鮮'),('螃蟹','海鮮'),('鱉','海鮮')
            ON CONFLICT (Name) DO NOTHING;
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductSections (
                Id          SERIAL PRIMARY KEY,
                ProductId   INTEGER NOT NULL,
                SectionName TEXT NOT NULL,
                SectionText TEXT NOT NULL,
                UNIQUE (ProductId, SectionName)
            );
            """);

        // Idempotent column additions for existing deployments
        foreach (var (col, def) in new[]
        {
            ("NutritionText", "TEXT NOT NULL DEFAULT ''"),
            ("CaloriesText",  "TEXT"),
            ("ProteinPct",    "DOUBLE PRECISION"),
            ("FatPct",        "DOUBLE PRECISION"),
            ("FiberPct",      "DOUBLE PRECISION"),
            ("Brand",          "TEXT NOT NULL DEFAULT ''"),
            ("BrandEn",        "TEXT NOT NULL DEFAULT ''"),
            ("BrandZh",        "TEXT NOT NULL DEFAULT ''"),
            ("PetType",        "TEXT NOT NULL DEFAULT ''"),
            ("LifeStage",      "TEXT NOT NULL DEFAULT ''"),
            ("IsPrescription", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("Functional",     "TEXT NOT NULL DEFAULT '[]'"),
            ("Special",        "TEXT NOT NULL DEFAULT '[]'"),
            ("ImageUrl",       "TEXT"),
            ("Price",          "INTEGER"),
            ("Volume",         "TEXT"),
        })
        {
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");
        }
    }

    public List<IngredientDto> GetIngredients()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name, Category FROM Ingredients ORDER BY Category, Name;";
        using var reader = cmd.ExecuteReader();
        var result = new List<IngredientDto>();
        while (reader.Read())
            result.Add(new IngredientDto(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public List<BrandDto> GetBrands()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Brand, BrandEn, BrandZh, COUNT(*)::int
            FROM Products
            WHERE Brand != ''
            GROUP BY Brand, BrandEn, BrandZh
            ORDER BY Brand;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<BrandDto>();
        while (reader.Read())
            result.Add(new BrandDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
    }

    // ── Slug ↔ DB value mappings ────────────────────────────────────────────
    private static string? SlugToPetType(string s) => s switch { "dog" => "狗", "cat" => "貓", _ => null };
    private static string? SlugToLifeStage(string s) => s switch
    {
        "kitten" => "幼犬", "adult" => "成犬", "senior" => "老犬", "all" => "全齡", _ => null
    };
    private static string PetTypeToSlug(string s) => s switch { "狗" => "dog", "貓" => "cat", _ => "other" };
    private static string LifeStageToSlug(string s) => s switch
    {
        "幼犬" or "幼貓" => "kitten", "成犬" or "成貓" => "adult",
        "老犬" or "老貓" => "senior", "全齡" => "all", _ => "other"
    };
    private static string? FormatPct(double? v)      => v.HasValue ? $"{v.Value:0.##}%" : null;
    private static string? NormCalories(string? txt) => string.IsNullOrWhiteSpace(txt) ? null
        : txt.Replace("大卡", " kcal").Replace("  ", " ").Trim();

    private static string[] ParseJsonArr(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    private static ProductResponseDto MapRow(NpgsqlDataReader r) => new(
        Id:        r.GetInt32(0),
        Name:      r.GetString(1),
        Brand:     r.GetString(2),
        Type:      PetTypeToSlug(r.GetString(3)),
        TypeLabel: r.GetString(3),
        Form:      "dry",
        FormLabel: "乾糧",
        Age:       LifeStageToSlug(r.GetString(4)),
        AgeLabel:  r.GetString(4),
        Flavor:    r.IsDBNull(5) ? null : r.GetString(5),
        Functional: ParseJsonArr(r.GetString(6)),
        Special:    ParseJsonArr(r.GetString(7)),
        Volume:    r.IsDBNull(8)  ? null : r.GetString(8),
        Price:     r.IsDBNull(9)  ? null : r.GetInt32(9),
        Image:     r.IsDBNull(10) ? null : r.GetString(10),
        Nutrition: new NutritionDto(
            Protein:    FormatPct(r.IsDBNull(11) ? null : r.GetDouble(11)),
            Fat:        FormatPct(r.IsDBNull(12) ? null : r.GetDouble(12)),
            Carbs:      null,
            Phosphorus: null,
            Calories:   NormCalories(r.IsDBNull(13) ? null : r.GetString(13))
        )
    );

    private const string ProductCols = """
        p.Id, p.Title, p.Brand, p.PetType, p.LifeStage,
        p.IngredientsText, p.Functional, p.Special,
        p.Volume, p.Price, p.ImageUrl,
        p.ProteinPct, p.FatPct, p.CaloriesText
        """;

    public (List<ProductResponseDto> Items, int Total) GetAll(
        int page = 1, int limit = 24,
        List<string>? type    = null,
        string?       form    = null,
        List<string>? age     = null,
        List<string>? brand   = null,
        List<string>? flavor  = null,
        List<string>? func    = null,
        List<string>? special = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        page  = Math.Max(1, page);

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var cond = new List<string>();

        if (type is { Count: > 0 })
        {
            var dbVals = type.Select(SlugToPetType).Where(v => v != null).Distinct().ToList();
            if (dbVals.Count > 0)
            {
                var phs = dbVals.Select((_, i) => $"@type{i}");
                cond.Add($"p.PetType IN ({string.Join(",", phs)})");
                for (int i = 0; i < dbVals.Count; i++) cmd.Parameters.AddWithValue($"type{i}", dbVals[i]!);
            }
        }
        if (age is { Count: > 0 })
        {
            var dbVals = age.Select(SlugToLifeStage).Where(v => v != null).Distinct().ToList();
            if (dbVals.Count > 0)
            {
                var phs = dbVals.Select((_, i) => $"@age{i}");
                cond.Add($"p.LifeStage IN ({string.Join(",", phs)})");
                for (int i = 0; i < dbVals.Count; i++) cmd.Parameters.AddWithValue($"age{i}", dbVals[i]!);
            }
        }
        if (brand is { Count: > 0 })
        {
            var phs = brand.Select((_, i) => $"@brand{i}");
            cond.Add($"p.Brand IN ({string.Join(",", phs)})");
            for (int i = 0; i < brand.Count; i++) cmd.Parameters.AddWithValue($"brand{i}", brand[i]);
        }
        if (flavor is { Count: > 0 })
        {
            var sub = flavor.Select((_, i) => $"p.IngredientsText ILIKE @flv{i}");
            cond.Add($"({string.Join(" OR ", sub)})");
            for (int i = 0; i < flavor.Count; i++) cmd.Parameters.AddWithValue($"flv{i}", $"%{flavor[i]}%");
        }
        if (func is { Count: > 0 })
        {
            var sub = func.Select((_, i) => $"p.Functional ~ @func{i}");
            cond.Add($"({string.Join(" OR ", sub)})");
            for (int i = 0; i < func.Count; i++) cmd.Parameters.AddWithValue($"func{i}", $"\"{func[i]}\"");
        }
        if (special is { Count: > 0 })
        {
            var sub = special.Select((_, i) => $"p.Special ~ @spc{i}");
            cond.Add($"({string.Join(" OR ", sub)})");
            for (int i = 0; i < special.Count; i++) cmd.Parameters.AddWithValue($"spc{i}", $"\"{special[i]}\"");
        }

        var where  = cond.Count > 0 ? "WHERE " + string.Join(" AND ", cond) : "";
        var offset = (page - 1) * limit;

        // Total count (reuse same params)
        using var cntCmd = conn.CreateCommand();
        cntCmd.CommandText = $"SELECT COUNT(*) FROM Products p {where}";
        foreach (NpgsqlParameter p in cmd.Parameters)
            cntCmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
        var total = Convert.ToInt32(cntCmd.ExecuteScalar() ?? 0);

        cmd.CommandText = $"""
            SELECT {ProductCols}
            FROM Products p
            {where}
            ORDER BY p.Title
            LIMIT @limit OFFSET @offset;
            """;
        cmd.Parameters.AddWithValue("limit",  limit);
        cmd.Parameters.AddWithValue("offset", offset);

        var items = new List<ProductResponseDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) items.Add(MapRow(reader));

        return (items, total);
    }

    public ProductResponseDto? GetById(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        ProductResponseDto? dto;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {ProductCols} FROM Products p WHERE p.Id = @id;";
            cmd.Parameters.AddWithValue("id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            dto = MapRow(reader);
        }
        return dto;
    }

    // ── /api/filters ───────────────────────────────────────────────────────
    private static readonly (string Slug, string Label)[] FunctionalDefs =
    {
        ("kidney","腎臟保健"),("urinary","泌尿道保健"),("digest","腸胃保健"),
        ("skin","皮膚毛髮"),("joint","關節保健"),("hairball","化毛配方"),("weight","體重管理"),
    };
    private static readonly (string Slug, string Label)[] SpecialDefs =
    {
        ("grain-free","無穀"),("hypoallergenic","低敏"),
    };

    public FiltersDto GetFilters()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        List<FilterOptionDto> CountBy(string col, string where = "") =>
            ExecCount(conn, $"SELECT {col}, COUNT(*)::int FROM Products {(where == "" ? "" : "WHERE " + where)} GROUP BY {col} ORDER BY COUNT(*) DESC");

        // types
        var rawTypes = CountBy("PetType");
        var types = rawTypes.Select(r => new FilterOptionDto(PetTypeToSlug(r.Value), r.Value, r.Count)).ToList();

        // forms – static (all dry for now)
        int total = ExecScalar(conn, "SELECT COUNT(*)::int FROM Products");
        var forms = new List<FilterOptionDto> { new("dry", "乾糧", total) };

        // ages
        var rawAges = CountBy("LifeStage");
        var ages = rawAges.Select(r => new FilterOptionDto(LifeStageToSlug(r.Value), r.Value, r.Count)).ToList();

        // brands
        var brands = ExecCount(conn, """
            SELECT Brand, COUNT(*)::int FROM Products
            WHERE Brand != '' GROUP BY Brand ORDER BY COUNT(*) DESC
            """).Select(r => new FilterOptionDto(r.Value, r.Value, r.Count)).ToList();

        // functional counts
        var functional = FunctionalDefs.Select(d =>
            new FilterOptionDto(d.Slug, d.Label, ExecScalar(conn, $"SELECT COUNT(*)::int FROM Products WHERE Functional ~ '\"{ d.Slug}\"'"))
        ).Where(x => x.Count > 0).ToList();

        // special counts
        var special = SpecialDefs.Select(d =>
            new FilterOptionDto(d.Slug, d.Label, ExecScalar(conn, $"SELECT COUNT(*)::int FROM Products WHERE Special ~ '\"{ d.Slug}\"'"))
        ).Where(x => x.Count > 0).ToList();

        return new FiltersDto(types, forms, ages, brands, functional, special);
    }

    private static List<FilterOptionDto> ExecCount(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<FilterOptionDto>();
        while (r.Read()) list.Add(new FilterOptionDto(r.GetString(0), r.GetString(0), r.GetInt32(1)));
        return list;
    }

    private static int ExecScalar(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
