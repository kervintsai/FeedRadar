using Npgsql;

public class ProductRepository
{
    private readonly string _connectionString;

    public ProductRepository(string connectionString)
    {
        _connectionString = ParseConnectionString(connectionString);
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

    public void Upsert(Product product)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        // 1. Upsert product row, returning its Id
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (Url, Title, IngredientsText, NutritionText, CaloriesText, ProteinPct, FatPct, FiberPct, ScannedAt)
            VALUES (@url, @title, @ingredients, @nutrition, @calories, @protein, @fat, @fiber, @scannedAt)
            ON CONFLICT (Url) DO UPDATE SET
                Title           = EXCLUDED.Title,
                IngredientsText = EXCLUDED.IngredientsText,
                NutritionText   = EXCLUDED.NutritionText,
                CaloriesText    = EXCLUDED.CaloriesText,
                ProteinPct      = EXCLUDED.ProteinPct,
                FatPct          = EXCLUDED.FatPct,
                FiberPct        = EXCLUDED.FiberPct,
                ScannedAt       = EXCLUDED.ScannedAt
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("url",        product.Url);
        cmd.Parameters.AddWithValue("title",      product.Title);
        cmd.Parameters.AddWithValue("ingredients", product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition",  product.NutritionText);
        cmd.Parameters.AddWithValue("calories",   product.CaloriesText as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("protein",    product.ProteinPct as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat",        product.FatPct    as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber",      product.FiberPct  as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scannedAt",  DateTime.UtcNow.ToString("O"));
        var productId = (int)(cmd.ExecuteScalar() ?? 0);

        // 2. Replace product ingredients
        var delCmd = conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM ProductIngredients WHERE ProductId = @pid;";
        delCmd.Parameters.AddWithValue("pid", productId);
        delCmd.ExecuteNonQuery();

        for (int i = 0; i < product.Ingredients.Count; i++)
        {
            var ingredient = product.Ingredients[i];

            var ingCmd = conn.CreateCommand();
            ingCmd.CommandText = """
                INSERT INTO Ingredients (Name, BaseName) VALUES (@name, @baseName)
                ON CONFLICT (Name) DO UPDATE SET BaseName = EXCLUDED.BaseName
                RETURNING Id;
                """;
            ingCmd.Parameters.AddWithValue("name",     ingredient.Name);
            ingCmd.Parameters.AddWithValue("baseName", ingredient.BaseName);
            var ingredientId = (int)(ingCmd.ExecuteScalar() ?? 0);

            var piCmd = conn.CreateCommand();
            piCmd.CommandText = """
                INSERT INTO ProductIngredients (ProductId, IngredientId, SortOrder, Percentage, AmountText)
                VALUES (@pid, @iid, @order, @pct, @amt)
                ON CONFLICT DO NOTHING;
                """;
            piCmd.Parameters.AddWithValue("pid",   productId);
            piCmd.Parameters.AddWithValue("iid",   ingredientId);
            piCmd.Parameters.AddWithValue("order", i);
            piCmd.Parameters.AddWithValue("pct",   ingredient.Percentage as object ?? DBNull.Value);
            piCmd.Parameters.AddWithValue("amt",   ingredient.AmountText as object ?? DBNull.Value);
            piCmd.ExecuteNonQuery();
        }

        // 3. Replace product sections
        var delSectCmd = conn.CreateCommand();
        delSectCmd.CommandText = "DELETE FROM ProductSections WHERE ProductId = @pid;";
        delSectCmd.Parameters.AddWithValue("pid", productId);
        delSectCmd.ExecuteNonQuery();

        foreach (var (sectionName, sectionText) in product.Sections)
        {
            var sectCmd = conn.CreateCommand();
            sectCmd.CommandText = """
                INSERT INTO ProductSections (ProductId, SectionName, SectionText)
                VALUES (@pid, @name, @text)
                ON CONFLICT (ProductId, SectionName) DO UPDATE SET SectionText = EXCLUDED.SectionText;
                """;
            sectCmd.Parameters.AddWithValue("pid",  productId);
            sectCmd.Parameters.AddWithValue("name", sectionName);
            sectCmd.Parameters.AddWithValue("text", sectionText);
            sectCmd.ExecuteNonQuery();
        }
    }

    // Clears all product data and resets identity sequences before a fresh scan.
    public void TruncateAll()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        Exec(conn, "TRUNCATE ProductSections, ProductIngredients, Products, Ingredients RESTART IDENTITY;");
        Console.WriteLine("[Cleanup] All tables truncated.");
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
