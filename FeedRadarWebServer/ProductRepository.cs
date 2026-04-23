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
    }

    public List<string> GetIngredients() => new()
    {
        // 陸上動物
        "雞", "火雞", "鴨", "鵝", "鵪鶉",
        "豬", "牛", "羊", "鹿", "袋鼠", "兔", "鴯鶓",
        // 魚類
        "鮭魚", "鱈魚", "鯡魚", "鯖魚", "鮪魚", "鰹魚",
        "沙丁魚", "鯷魚", "鰈魚", "比目魚", "鱒魚",
        "鱸魚", "鰻魚", "平鮋", "虱目魚",
        // 海鮮
        "磷蝦", "貽貝", "干貝", "龍蝦", "螃蟹", "鱉",
    };

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
