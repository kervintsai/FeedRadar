using Microsoft.Data.Sqlite;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(IConfiguration config)
    {
        _connectionString = $"Data Source={config["Database:Path"] ?? "feeds.db"}";
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var create = conn.CreateCommand();
        create.CommandText = """
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
            """;
        create.ExecuteNonQuery();

        foreach (var (col, def) in new[]
        {
            ("NutritionText", "TEXT NOT NULL DEFAULT ''"),
            ("ProteinPct",    "REAL"),
            ("FatPct",        "REAL"),
            ("FiberPct",      "REAL"),
        })
        {
            try
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE Products ADD COLUMN {col} {def};";
                alter.ExecuteNonQuery();
            }
            catch { /* 欄位已存在，略過 */ }
        }
    }

    public List<ProductDto> GetAll(string? q = null, string? ingredient = null,
        double? minProtein = null, double? maxFat = null, double? maxFiber = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(q))
        {
            conditions.Add("(Title LIKE $q OR IngredientsText LIKE $q)");
            cmd.Parameters.AddWithValue("$q", $"%{q}%");
        }
        if (!string.IsNullOrWhiteSpace(ingredient))
        {
            conditions.Add("IngredientsText LIKE $ing");
            cmd.Parameters.AddWithValue("$ing", $"%{ingredient}%");
        }
        if (minProtein.HasValue)
        {
            conditions.Add("ProteinPct >= $minProtein");
            cmd.Parameters.AddWithValue("$minProtein", minProtein.Value);
        }
        if (maxFat.HasValue)
        {
            conditions.Add("FatPct <= $maxFat");
            cmd.Parameters.AddWithValue("$maxFat", maxFat.Value);
        }
        if (maxFiber.HasValue)
        {
            conditions.Add("FiberPct <= $maxFiber");
            cmd.Parameters.AddWithValue("$maxFiber", maxFiber.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT Id, Url, Title, IngredientsText, NutritionText,
                   ProteinPct, FatPct, FiberPct, ScannedAt
            FROM Products {where}
            ORDER BY Title;
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
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            reader.GetString(8)
        );
    }
}
