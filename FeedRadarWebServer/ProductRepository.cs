using Microsoft.Data.Sqlite;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(IConfiguration config)
    {
        var configuredPath = config["Database:Path"];
        var dbPath = string.IsNullOrEmpty(configuredPath)
            ? ResolveDefaultDbPath()
            : configuredPath;
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    private static string ResolveDefaultDbPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 ||
                Directory.Exists(Path.Combine(dir, ".git")))
                return Path.Combine(dir, "feeds.db");
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        return "feeds.db";
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Products (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Url             TEXT UNIQUE NOT NULL,
                Title           TEXT NOT NULL,
                IngredientsText TEXT NOT NULL DEFAULT '',
                NutritionText   TEXT NOT NULL DEFAULT '',
                ProteinPct      REAL,
                FatPct          REAL,
                FiberPct        REAL,
                ScannedAt       TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Ingredients (
                Id   INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT UNIQUE NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductIngredients (
                ProductId    INTEGER NOT NULL,
                IngredientId INTEGER NOT NULL,
                SortOrder    INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (ProductId, IngredientId)
            );
            """);

        foreach (var (col, def) in new[]
        {
            ("NutritionText", "TEXT NOT NULL DEFAULT ''"),
            ("ProteinPct",    "REAL"),
            ("FatPct",        "REAL"),
            ("FiberPct",      "REAL"),
        })
        {
            try { Exec(conn, $"ALTER TABLE Products ADD COLUMN {col} {def};"); }
            catch { /* 已存在，略過 */ }
        }
    }

    // 所有不重複的成分名稱，給前端做篩選下拉選單用
    public List<string> GetIngredients()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Ingredients ORDER BY Name;";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public List<ProductDto> GetAll(string? q = null, string? ingredient = null,
        double? minProtein = null, double? maxFat = null, double? maxFiber = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();

        // 用 JOIN 做精確成分篩選
        var fromClause = "FROM Products p";
        if (!string.IsNullOrWhiteSpace(ingredient))
        {
            fromClause = """
                FROM Products p
                JOIN ProductIngredients pi ON pi.ProductId = p.Id
                JOIN Ingredients i ON i.Id = pi.IngredientId
                """;
            conditions.Add("i.Name = $ingredient");
            cmd.Parameters.AddWithValue("$ingredient", ingredient);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            conditions.Add("(p.Title LIKE $q OR p.IngredientsText LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{q}%");
        }
        if (minProtein.HasValue)
        {
            conditions.Add("p.ProteinPct >= $minProtein");
            cmd.Parameters.AddWithValue("$minProtein", minProtein.Value);
        }
        if (maxFat.HasValue)
        {
            conditions.Add("p.FatPct <= $maxFat");
            cmd.Parameters.AddWithValue("$maxFat", maxFat.Value);
        }
        if (maxFiber.HasValue)
        {
            conditions.Add("p.FiberPct <= $maxFiber");
            cmd.Parameters.AddWithValue("$maxFiber", maxFiber.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT DISTINCT p.Id, p.Url, p.Title, p.IngredientsText, p.NutritionText,
                   p.ProteinPct, p.FatPct, p.FiberPct, p.ScannedAt
            {fromClause}
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
                reader.GetString(8)
            ));
        }
        return results;
    }

    public ProductDto? GetById(int id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Url, Title, IngredientsText, NutritionText,
                   ProteinPct, FatPct, FiberPct, ScannedAt
            FROM Products WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new ProductDto(
            reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.GetString(8)
        );
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
