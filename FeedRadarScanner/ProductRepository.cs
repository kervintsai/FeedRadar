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

    public void Upsert(Product product)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 1. upsert product
        var cmd = conn.CreateCommand();
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

        // 2. get product id
        var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT Id FROM Products WHERE Url = $url;";
        idCmd.Parameters.AddWithValue("$url", product.Url);
        var productId = (long)(idCmd.ExecuteScalar() ?? 0);

        // 3. 清掉舊的 product ingredients，重新插入
        var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM ProductIngredients WHERE ProductId = $pid;";
        delCmd.Parameters.AddWithValue("$pid", productId);
        delCmd.ExecuteNonQuery();

        // 4. upsert 每個成分
        for (int i = 0; i < product.Ingredients.Count; i++)
        {
            var name = product.Ingredients[i];

            var ingCmd = conn.CreateCommand();
            ingCmd.CommandText = """
                INSERT INTO Ingredients (Name) VALUES ($name)
                ON CONFLICT(Name) DO NOTHING;
                """;
            ingCmd.Parameters.AddWithValue("$name", name);
            ingCmd.ExecuteNonQuery();

            var ingIdCmd = conn.CreateCommand();
            ingIdCmd.CommandText = "SELECT Id FROM Ingredients WHERE Name = $name;";
            ingIdCmd.Parameters.AddWithValue("$name", name);
            var ingredientId = (long)(ingIdCmd.ExecuteScalar() ?? 0);

            var piCmd = conn.CreateCommand();
            piCmd.CommandText = """
                INSERT OR IGNORE INTO ProductIngredients (ProductId, IngredientId, SortOrder)
                VALUES ($pid, $iid, $order);
                """;
            piCmd.Parameters.AddWithValue("$pid", productId);
            piCmd.Parameters.AddWithValue("$iid", ingredientId);
            piCmd.Parameters.AddWithValue("$order", i);
            piCmd.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
