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

    public List<ProductDto> GetAll(string? search = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (string.IsNullOrWhiteSpace(search))
        {
            cmd.CommandText = "SELECT Id, Url, Title, IngredientsText, ScannedAt FROM Products ORDER BY Title;";
        }
        else
        {
            cmd.CommandText = """
                SELECT Id, Url, Title, IngredientsText, ScannedAt FROM Products
                WHERE Title LIKE $q OR IngredientsText LIKE $q
                ORDER BY Title;
                """;
            cmd.Parameters.AddWithValue("$q", $"%{search}%");
        }

        var results = new List<ProductDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ProductDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)
            ));
        }
        return results;
    }

    public ProductDto? GetById(int id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Url, Title, IngredientsText, ScannedAt FROM Products WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ProductDto(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4)
        );
    }
}
