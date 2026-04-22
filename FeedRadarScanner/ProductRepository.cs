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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Products (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Url             TEXT UNIQUE NOT NULL,
                Title           TEXT NOT NULL,
                IngredientsText TEXT NOT NULL DEFAULT '',
                ScannedAt       TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Upsert(Product product)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (Url, Title, IngredientsText, ScannedAt)
            VALUES ($url, $title, $ingredients, $scannedAt)
            ON CONFLICT(Url) DO UPDATE SET
                Title           = excluded.Title,
                IngredientsText = excluded.IngredientsText,
                ScannedAt       = excluded.ScannedAt;
            """;
        cmd.Parameters.AddWithValue("$url", product.Url);
        cmd.Parameters.AddWithValue("$title", product.Title);
        cmd.Parameters.AddWithValue("$ingredients", product.IngredientsText);
        cmd.Parameters.AddWithValue("$scannedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
