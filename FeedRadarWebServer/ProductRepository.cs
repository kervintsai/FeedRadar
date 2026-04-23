using Npgsql;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(IConfiguration config)
    {
        // Railway 注入 DATABASE_URL；本地開發用 appsettings ConnectionStrings:Default
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        _connectionString = !string.IsNullOrEmpty(databaseUrl)
            ? databaseUrl
            : config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("No database connection string configured.");
        EnsureSchema();
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
                ProteinPct      DOUBLE PRECISION,
                FatPct          DOUBLE PRECISION,
                FiberPct        DOUBLE PRECISION,
                ScannedAt       TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Ingredients (
                Id   SERIAL PRIMARY KEY,
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
            ("ProteinPct",    "DOUBLE PRECISION"),
            ("FatPct",        "DOUBLE PRECISION"),
            ("FiberPct",      "DOUBLE PRECISION"),
        })
        {
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");
        }
    }

    public List<string> GetIngredients()
    {
        using var conn = new NpgsqlConnection(_connectionString);
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
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();

        var fromClause = "FROM Products p";
        if (!string.IsNullOrWhiteSpace(ingredient))
        {
            fromClause = """
                FROM Products p
                JOIN ProductIngredients pi ON pi.ProductId = p.Id
                JOIN Ingredients i ON i.Id = pi.IngredientId
                """;
            conditions.Add("i.Name = @ingredient");
            cmd.Parameters.AddWithValue("ingredient", ingredient);
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
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Url, Title, IngredientsText, NutritionText,
                   ProteinPct, FatPct, FiberPct, ScannedAt
            FROM Products WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("id", id);
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

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
