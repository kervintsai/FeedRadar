using Microsoft.Data.Sqlite;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(string dbPath = "feeds.db")
    {
        _connectionString = $"Data Source={dbPath}";
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

        // 舊資料庫 migration：逐一嘗試加欄位，已存在就忽略
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

    public void Upsert(Product product)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (Url, Title, IngredientsText, NutritionText, ProteinPct, FatPct, FiberPct, ScannedAt)
            VALUES ($url, $title, $ingredients, $nutrition, $protein, $fat, $fiber, $scannedAt)
            ON CONFLICT(Url) DO UPDATE SET
                Title           = excluded.Title,
                IngredientsText = excluded.IngredientsText,
                NutritionText   = excluded.NutritionText,
                ProteinPct      = excluded.ProteinPct,
                FatPct          = excluded.FatPct,
                FiberPct        = excluded.FiberPct,
                ScannedAt       = excluded.ScannedAt;
            """;
        cmd.Parameters.AddWithValue("$url", product.Url);
        cmd.Parameters.AddWithValue("$title", product.Title);
        cmd.Parameters.AddWithValue("$ingredients", product.IngredientsText);
        cmd.Parameters.AddWithValue("$nutrition", product.NutritionText);
        cmd.Parameters.AddWithValue("$protein", product.ProteinPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fat", product.FatPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fiber", product.FiberPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$scannedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
