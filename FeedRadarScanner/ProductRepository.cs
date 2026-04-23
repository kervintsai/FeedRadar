using Npgsql;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(string connectionString)
    {
        _connectionString = connectionString;
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

    public void Upsert(Product product)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        // 1. upsert product，回傳 Id
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (Url, Title, IngredientsText, NutritionText, ProteinPct, FatPct, FiberPct, ScannedAt)
            VALUES (@url, @title, @ingredients, @nutrition, @protein, @fat, @fiber, @scannedAt)
            ON CONFLICT (Url) DO UPDATE SET
                Title           = EXCLUDED.Title,
                IngredientsText = EXCLUDED.IngredientsText,
                NutritionText   = EXCLUDED.NutritionText,
                ProteinPct      = EXCLUDED.ProteinPct,
                FatPct          = EXCLUDED.FatPct,
                FiberPct        = EXCLUDED.FiberPct,
                ScannedAt       = EXCLUDED.ScannedAt
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("url", product.Url);
        cmd.Parameters.AddWithValue("title", product.Title);
        cmd.Parameters.AddWithValue("ingredients", product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition", product.NutritionText);
        cmd.Parameters.AddWithValue("protein", product.ProteinPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat", product.FatPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber", product.FiberPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scannedAt", DateTime.UtcNow.ToString("O"));
        var productId = (int)(cmd.ExecuteScalar() ?? 0);

        // 2. 清掉舊的 product ingredients，重新插入
        var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM ProductIngredients WHERE ProductId = @pid;";
        delCmd.Parameters.AddWithValue("pid", productId);
        delCmd.ExecuteNonQuery();

        // 3. upsert 每個成分
        for (int i = 0; i < product.Ingredients.Count; i++)
        {
            var name = product.Ingredients[i];

            // 用 fake update 讓 RETURNING 在衝突時也能回傳 Id
            var ingCmd = conn.CreateCommand();
            ingCmd.CommandText = """
                INSERT INTO Ingredients (Name) VALUES (@name)
                ON CONFLICT (Name) DO UPDATE SET Name = EXCLUDED.Name
                RETURNING Id;
                """;
            ingCmd.Parameters.AddWithValue("name", name);
            var ingredientId = (int)(ingCmd.ExecuteScalar() ?? 0);

            var piCmd = conn.CreateCommand();
            piCmd.CommandText = """
                INSERT INTO ProductIngredients (ProductId, IngredientId, SortOrder)
                VALUES (@pid, @iid, @order)
                ON CONFLICT DO NOTHING;
                """;
            piCmd.Parameters.AddWithValue("pid", productId);
            piCmd.Parameters.AddWithValue("iid", ingredientId);
            piCmd.Parameters.AddWithValue("order", i);
            piCmd.ExecuteNonQuery();
        }
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
