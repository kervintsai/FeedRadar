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
                ScannedAt       TEXT NOT NULL,
                BrandSlug       TEXT,
                TypeSlug        TEXT,
                FormSlug        TEXT,
                AgeSlug         TEXT,
                Volume          TEXT,
                Price           INTEGER,
                ImageUrl        TEXT,
                PhosphorusMg    DOUBLE PRECISION,
                MoisturePct     DOUBLE PRECISION
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Ingredients (
                Id       SERIAL PRIMARY KEY,
                Name     TEXT UNIQUE NOT NULL,
                BaseName TEXT NOT NULL DEFAULT ''
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductIngredients (
                ProductId    INTEGER NOT NULL,
                IngredientId INTEGER NOT NULL,
                SortOrder    INTEGER NOT NULL DEFAULT 0,
                Percentage   DOUBLE PRECISION,
                AmountText   TEXT,
                PRIMARY KEY (ProductId, IngredientId)
            );
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
            ("ProteinPct",    "DOUBLE PRECISION"),
            ("FatPct",        "DOUBLE PRECISION"),
            ("FiberPct",      "DOUBLE PRECISION"),
        })
        {
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");
        }

        Exec(conn, "ALTER TABLE ProductIngredients ADD COLUMN IF NOT EXISTS Percentage DOUBLE PRECISION;");
        Exec(conn, "ALTER TABLE ProductIngredients ADD COLUMN IF NOT EXISTS AmountText TEXT;");
        Exec(conn, "ALTER TABLE Ingredients ADD COLUMN IF NOT EXISTS BaseName TEXT NOT NULL DEFAULT ''");
        Exec(conn, "ALTER TABLE Products ADD COLUMN IF NOT EXISTS CaloriesText TEXT;");

        foreach (var (col, def) in new[]
        {
            ("BrandSlug",    "TEXT"),
            ("TypeSlug",     "TEXT"),
            ("FormSlug",     "TEXT"),
            ("AgeSlug",      "TEXT"),
            ("Volume",       "TEXT"),
            ("Price",        "INTEGER"),
            ("ImageUrl",     "TEXT"),
            ("PhosphorusMg", "DOUBLE PRECISION"),
            ("MoisturePct",  "DOUBLE PRECISION"),
        })
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Brands (
                Slug  TEXT PRIMARY KEY,
                Label TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Flavors (
                Slug  TEXT PRIMARY KEY,
                Label TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductFlavors (
                ProductId   INTEGER NOT NULL,
                FlavorSlug  TEXT    NOT NULL,
                PRIMARY KEY (ProductId, FlavorSlug)
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductFunctional (
                ProductId      INTEGER NOT NULL,
                FunctionalSlug TEXT    NOT NULL,
                PRIMARY KEY (ProductId, FunctionalSlug)
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductSpecial (
                ProductId   INTEGER NOT NULL,
                SpecialSlug TEXT    NOT NULL,
                PRIMARY KEY (ProductId, SpecialSlug)
            );
            """);

        SeedLookups(conn);
    }

    private static void SeedLookups(NpgsqlConnection conn)
    {
        var brands = new[]
        {
            ("wangmiao",    "汪喵星球"), ("alleycat",  "巷弄貓"),
            ("nutrience",   "紐崔斯"),   ("ziwi",      "巔峰"),
            ("ziwipeak",    "Ziwi"),     ("schesir",   "Schesir"),
            ("almo",        "Almo Nature"), ("applaws", "Applaws"),
            ("weruva",      "Weruva"),   ("tikicat",   "Tiki Cat"),
            ("cesar",       "西莎"),     ("hills",     "Hill's"),
        };
        foreach (var (slug, label) in brands)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Brands(Slug,Label) VALUES(@s,@l) ON CONFLICT DO NOTHING;";
            cmd.Parameters.AddWithValue("s", slug);
            cmd.Parameters.AddWithValue("l", label);
            cmd.ExecuteNonQuery();
        }

        var flavors = new[]
        {
            ("chicken", "雞肉"), ("beef",   "牛肉"), ("fish",    "魚肉"),
            ("tuna",    "鮪魚"), ("turkey", "火雞"), ("lamb",    "羊肉"),
            ("duck",    "鴨肉"), ("salmon", "鮭魚"), ("venison", "鹿肉"),
            ("rabbit",  "兔肉"), ("quail",  "鵪鶉"), ("mixed",   "綜合"),
        };
        foreach (var (slug, label) in flavors)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Flavors(Slug,Label) VALUES(@s,@l) ON CONFLICT DO NOTHING;";
            cmd.Parameters.AddWithValue("s", slug);
            cmd.Parameters.AddWithValue("l", label);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Filters ──────────────────────────────────────────────────────────────

    public FiltersData GetFilters()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        int CountWhere(NpgsqlConnection c, string sql, string param, string val)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("v", val);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        var types = new[] { ("cat","貓"), ("dog","狗") }
            .Select(x => new FilterItemDto(x.Item1, x.Item2,
                CountWhere(conn, "SELECT COUNT(*) FROM Products WHERE TypeSlug=@v", "v", x.Item1)))
            .ToList();

        var forms = new[] { ("wet","濕食"), ("dry","乾糧") }
            .Select(x => new FilterItemDto(x.Item1, x.Item2,
                CountWhere(conn, "SELECT COUNT(*) FROM Products WHERE FormSlug=@v", "v", x.Item1)))
            .ToList();

        var ages = new[]
        {
            ("kitten","幼貓/幼犬"), ("adult","成貓/成犬"),
            ("senior","老貓/老犬"), ("all","全齡"),
        }
        .Select(x => new FilterItemDto(x.Item1, x.Item2,
            CountWhere(conn, "SELECT COUNT(*) FROM Products WHERE AgeSlug=@v", "v", x.Item1)))
        .ToList();

        var functionalDefs = new[]
        {
            ("kidney","腎臟保健"), ("urinary","泌尿道保健"), ("digest","腸胃保健"),
            ("skin","皮膚毛髮"), ("joint","關節保健"), ("hairball","化毛配方"), ("weight","體重管理"),
        };
        var functional = functionalDefs.Select(x => new FilterItemDto(x.Item1, x.Item2,
            CountWhere(conn, "SELECT COUNT(*) FROM ProductFunctional WHERE FunctionalSlug=@v", "v", x.Item1)))
            .ToList();

        var specialDefs = new[] { ("grain-free","無穀"), ("hypoallergenic","低敏") };
        var special = specialDefs.Select(x => new FilterItemDto(x.Item1, x.Item2,
            CountWhere(conn, "SELECT COUNT(*) FROM ProductSpecial WHERE SpecialSlug=@v", "v", x.Item1)))
            .ToList();

        // Brands: join with Brands table, order by count desc
        var brands = new List<FilterItemDto>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT b.Slug, b.Label, COUNT(p.Id) AS cnt
                FROM Brands b
                LEFT JOIN Products p ON p.BrandSlug = b.Slug
                GROUP BY b.Slug, b.Label
                HAVING COUNT(p.Id) > 0
                ORDER BY cnt DESC;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                brands.Add(new FilterItemDto(r.GetString(0), r.GetString(1), (int)r.GetInt64(2)));
        }

        // Flavors: join with Flavors table, order by count desc
        var flavors = new List<FilterItemDto>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.Slug, f.Label, COUNT(pf.ProductId) AS cnt
                FROM Flavors f
                LEFT JOIN ProductFlavors pf ON pf.FlavorSlug = f.Slug
                GROUP BY f.Slug, f.Label
                HAVING COUNT(pf.ProductId) > 0
                ORDER BY cnt DESC;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                flavors.Add(new FilterItemDto(r.GetString(0), r.GetString(1), (int)r.GetInt64(2)));
        }

        return new FiltersData(types, forms, ages, brands, flavors, functional, special);
    }

    // ── Products list ─────────────────────────────────────────────────────────

    public (List<ApiProductDto> Products, int Total) GetAll(
        string? type, string? form, string? age,
        string? brand, string? flavor, string? func, string? special,
        int page, int limit)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        var conditions = new List<string>();
        var cmd = conn.CreateCommand();

        void AddSlugFilter(string column, string? csv, string prefix)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            var values = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0) return;
            var paramNames = values.Select((v, i) => { var p = $"{prefix}{i}"; cmd.Parameters.AddWithValue(p, v); return $"@{p}"; });
            conditions.Add($"p.{column} IN ({string.Join(",", paramNames)})");
        }

        void AddJoinFilter(string joinTable, string joinCol, string? csv, string prefix)
        {
            if (string.IsNullOrWhiteSpace(csv)) return;
            var values = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0) return;
            var paramNames = values.Select((v, i) => { var p = $"{prefix}{i}"; cmd.Parameters.AddWithValue(p, v); return $"@{p}"; });
            conditions.Add($"EXISTS (SELECT 1 FROM {joinTable} x WHERE x.ProductId=p.Id AND x.{joinCol} IN ({string.Join(",", paramNames)}))");
        }

        AddSlugFilter("TypeSlug", type,  "type");
        AddSlugFilter("FormSlug", form,  "form");
        AddSlugFilter("AgeSlug",  age,   "age");
        AddSlugFilter("BrandSlug", brand, "brand");
        AddJoinFilter("ProductFlavors",    "FlavorSlug",   flavor,  "fl");
        AddJoinFilter("ProductFunctional", "FunctionalSlug", func,  "fn");
        AddJoinFilter("ProductSpecial",    "SpecialSlug",   special, "sp");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        // Total count
        var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(DISTINCT p.Id) FROM Products p {where}";
        foreach (NpgsqlParameter p in cmd.Parameters)
        {
            var cp = countCmd.CreateParameter();
            cp.ParameterName = p.ParameterName;
            cp.Value = p.Value;
            countCmd.Parameters.Add(cp);
        }
        var total = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        // Paginated products
        cmd.CommandText = $"""
            SELECT DISTINCT p.Id, p.Title, p.BrandSlug, p.TypeSlug, p.FormSlug, p.AgeSlug,
                   p.Volume, p.Price, p.ImageUrl,
                   p.ProteinPct, p.FatPct, p.MoisturePct, p.PhosphorusMg, p.CaloriesText, p.FiberPct
            FROM Products p
            {where}
            ORDER BY p.Title
            LIMIT @lim OFFSET @off;
            """;
        cmd.Parameters.AddWithValue("lim", limit);
        cmd.Parameters.AddWithValue("off", (page - 1) * limit);

        var products = new List<ApiProductDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            products.Add(MapProduct(conn, id, reader));
        }

        return (products, total);
    }

    private ApiProductDto MapProduct(NpgsqlConnection conn, int id, NpgsqlDataReader r)
    {
        var typeSlug  = r.IsDBNull(3) ? null : r.GetString(3);
        var formSlug  = r.IsDBNull(4) ? null : r.GetString(4);
        var ageSlug   = r.IsDBNull(5) ? null : r.GetString(5);
        var brandSlug = r.IsDBNull(2) ? null : r.GetString(2);

        var brandLabel = brandSlug is null ? null : GetBrandLabel(conn, brandSlug);

        static string? TypeLabel(string? s) => s switch { "cat" => "貓", "dog" => "狗", _ => null };
        static string? FormLabel(string? s) => s switch { "wet" => "濕食", "dry" => "乾糧", _ => null };
        static string? AgeLabel(string? s)  => s switch
        {
            "kitten" => "幼貓/幼犬", "adult" => "成貓/成犬",
            "senior" => "老貓/老犬", "all"   => "全齡", _ => null
        };

        var proteinPct = r.IsDBNull(9)  ? (double?)null : r.GetDouble(9);
        var fatPct     = r.IsDBNull(10) ? (double?)null : r.GetDouble(10);
        var moistPct   = r.IsDBNull(11) ? (double?)null : r.GetDouble(11);
        var phosphoMg  = r.IsDBNull(12) ? (double?)null : r.GetDouble(12);
        var calText    = r.IsDBNull(13) ? null : r.GetString(13);
        var fiberPct   = r.IsDBNull(14) ? (double?)null : r.GetDouble(14);

        double? carbsPct = null;
        if (proteinPct.HasValue && fatPct.HasValue && moistPct.HasValue)
        {
            var ash = 2.0; // approximate when unknown
            carbsPct = Math.Max(0, 100 - proteinPct.Value - fatPct.Value - moistPct.Value - ash);
        }

        string? FormatPct(double? v) => v.HasValue ? $"{v.Value:0.##}%" : null;
        string? FormatPhospho(double? v) => v.HasValue ? $"{v.Value:0.##} mg/100kcal" : null;
        string? FormatCalories(string? raw)
        {
            if (raw is null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"(\d+(?:\.\d+)?)");
            return m.Success ? $"{m.Groups[1].Value} kcal/100g" : null;
        }

        var flavors    = GetStrings(conn, "SELECT f.Label FROM ProductFlavors pf JOIN Flavors f ON f.Slug=pf.FlavorSlug WHERE pf.ProductId=@id", id);
        var functional = GetStrings(conn, """
            SELECT CASE FunctionalSlug
              WHEN 'kidney'   THEN '腎臟保健' WHEN 'urinary'  THEN '泌尿道保健'
              WHEN 'digest'   THEN '腸胃保健' WHEN 'skin'     THEN '皮膚毛髮'
              WHEN 'joint'    THEN '關節保健' WHEN 'hairball' THEN '化毛配方'
              WHEN 'weight'   THEN '體重管理' ELSE FunctionalSlug END
            FROM ProductFunctional WHERE ProductId=@id
            """, id);
        var special    = GetStrings(conn, """
            SELECT CASE SpecialSlug
              WHEN 'grain-free'      THEN '無穀'
              WHEN 'hypoallergenic'  THEN '低敏' ELSE SpecialSlug END
            FROM ProductSpecial WHERE ProductId=@id
            """, id);

        return new ApiProductDto(
            Id:           $"prod_{id:D3}",
            Name:         r.GetString(1),
            Brand:        brandLabel,
            Type:         typeSlug,
            TypeLabel:    TypeLabel(typeSlug),
            Form:         formSlug,
            FormLabel:    FormLabel(formSlug),
            Age:          ageSlug,
            AgeLabel:     AgeLabel(ageSlug),
            Flavors:      flavors,
            Functional:   functional,
            Special:      special,
            Volume:       r.IsDBNull(6) ? null : r.GetString(6),
            Price:        r.IsDBNull(7) ? (int?)null : r.GetInt32(7),
            Image:        r.IsDBNull(8) ? null : r.GetString(8),
            Nutrition: new NutritionDto(
                Protein:    FormatPct(proteinPct),
                Fat:        FormatPct(fatPct),
                Fiber:      FormatPct(fiberPct),
                Carbs:      FormatPct(carbsPct),
                Phosphorus: FormatPhospho(phosphoMg),
                Calories:   FormatCalories(calText)
            )
        );
    }

    private static string? GetBrandLabel(NpgsqlConnection conn, string slug)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Label FROM Brands WHERE Slug=@s";
        cmd.Parameters.AddWithValue("s", slug);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private static List<string> GetStrings(NpgsqlConnection conn, string sql, int productId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", productId);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<string> GetIngredients()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT BaseName FROM Ingredients WHERE BaseName != '' ORDER BY BaseName;";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public List<ProductDto> GetAll(string? q = null, List<string>? ingredients = null,
        double? minProtein = null, double? maxFat = null, double? maxFiber = null)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();

        // Each selected ingredient becomes an AND subquery:
        // product must contain an ingredient whose BaseName matches each keyword.
        if (ingredients is { Count: > 0 })
        {
            for (int idx = 0; idx < ingredients.Count; idx++)
            {
                var p = $"ing{idx}";
                conditions.Add($"""
                    p.Id IN (
                        SELECT pi2.ProductId
                        FROM ProductIngredients pi2
                        JOIN Ingredients i2 ON i2.Id = pi2.IngredientId
                        WHERE i2.BaseName ILIKE @{p}
                    )
                    """);
                cmd.Parameters.AddWithValue(p, $"%{ingredients[idx]}%");
            }
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            conditions.Add("(p.Title ILIKE @q OR p.IngredientsText ILIKE @q)");
            cmd.Parameters.AddWithValue("q", $"%{q}%");
        }
        if (minProtein.HasValue)
        {
            conditions.Add("p.ProteinPct >= @minProtein");
            cmd.Parameters.AddWithValue("minProtein", minProtein.Value);
        }
        if (maxFat.HasValue)
        {
            conditions.Add("p.FatPct <= @maxFat");
            cmd.Parameters.AddWithValue("maxFat", maxFat.Value);
        }
        if (maxFiber.HasValue)
        {
            conditions.Add("p.FiberPct <= @maxFiber");
            cmd.Parameters.AddWithValue("maxFiber", maxFiber.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT DISTINCT p.Id, p.Url, p.Title, p.IngredientsText, p.NutritionText,
                   p.ProteinPct, p.FatPct, p.FiberPct, p.ScannedAt, p.CaloriesText
            FROM Products p
            {where}
            ORDER BY p.Title;
            """;

        var results = new List<ProductDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ProductDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.GetString(8),
                CaloriesText: reader.IsDBNull(9) ? null : reader.GetString(9)
            ));
        }
        return results;
    }

    public ProductDto? GetById(int id)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        // Fetch product row
        ProductDto? dto;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, Url, Title, IngredientsText, NutritionText,
                       ProteinPct, FatPct, FiberPct, ScannedAt, CaloriesText
                FROM Products WHERE Id = @id;
                """;
            cmd.Parameters.AddWithValue("id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            dto = new ProductDto(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.GetString(8),
                CaloriesText: reader.IsDBNull(9) ? null : reader.GetString(9)
            );
        }

        // Fetch all sections for this product
        var sections = new Dictionary<string, string>();
        using (var sectCmd = conn.CreateCommand())
        {
            sectCmd.CommandText = """
                SELECT SectionName, SectionText
                FROM ProductSections
                WHERE ProductId = @id
                ORDER BY Id;
                """;
            sectCmd.Parameters.AddWithValue("id", id);
            using var sectReader = sectCmd.ExecuteReader();
            while (sectReader.Read())
                sections[sectReader.GetString(0)] = sectReader.GetString(1);
        }

        return dto with { Sections = sections };
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
