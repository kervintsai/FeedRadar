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
        var uri   = new Uri(s);
        var parts = uri.UserInfo.Split(':', 2);
        var user  = Uri.UnescapeDataString(parts[0]);
        var pass  = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        var db    = uri.AbsolutePath.TrimStart('/');
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
                Title           TEXT NOT NULL DEFAULT '',
                Brand           TEXT NOT NULL DEFAULT '',
                PetType         TEXT NOT NULL DEFAULT '',
                IsPrescription  BOOLEAN NOT NULL DEFAULT FALSE,
                Form            TEXT NOT NULL DEFAULT '',
                IngredientsText TEXT NOT NULL DEFAULT '',
                NutritionText   TEXT NOT NULL DEFAULT '',
                CaloriesText    TEXT,
                ProteinPct      DOUBLE PRECISION,
                FatPct          DOUBLE PRECISION,
                FiberPct        DOUBLE PRECISION,
                ScannedAt       TEXT NOT NULL DEFAULT ''
            );
            """);

        foreach (var (col, def) in new[]
        {
            ("Brand",          "TEXT NOT NULL DEFAULT ''"),
            ("PetType",        "TEXT NOT NULL DEFAULT ''"),
            ("IsPrescription", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("Form",           "TEXT NOT NULL DEFAULT ''"),
            ("NutritionText",  "TEXT NOT NULL DEFAULT ''"),
            ("CaloriesText",   "TEXT"),
            ("ProteinPct",     "DOUBLE PRECISION"),
            ("FatPct",         "DOUBLE PRECISION"),
            ("FiberPct",       "DOUBLE PRECISION"),
        })
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");
    }

    public void Upsert(Product product)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (
                Url, Title, Brand, PetType, IsPrescription, Form,
                IngredientsText, NutritionText, CaloriesText,
                ProteinPct, FatPct, FiberPct, ScannedAt
            )
            VALUES (
                @url, @title, @brand, @petType, @isPrescription, @form,
                @ingredients, @nutrition, @calories,
                @protein, @fat, @fiber, @scannedAt
            )
            ON CONFLICT (Url) DO UPDATE SET
                Title           = EXCLUDED.Title,
                Brand           = EXCLUDED.Brand,
                PetType         = EXCLUDED.PetType,
                IsPrescription  = EXCLUDED.IsPrescription,
                Form            = EXCLUDED.Form,
                IngredientsText = EXCLUDED.IngredientsText,
                NutritionText   = EXCLUDED.NutritionText,
                CaloriesText    = EXCLUDED.CaloriesText,
                ProteinPct      = EXCLUDED.ProteinPct,
                FatPct          = EXCLUDED.FatPct,
                FiberPct        = EXCLUDED.FiberPct,
                ScannedAt       = EXCLUDED.ScannedAt;
            """;
        cmd.Parameters.AddWithValue("url",            product.Url);
        cmd.Parameters.AddWithValue("title",          product.Title);
        cmd.Parameters.AddWithValue("brand",          product.Brand);
        cmd.Parameters.AddWithValue("petType",        product.PetType);
        cmd.Parameters.AddWithValue("isPrescription", product.IsPrescription);
        cmd.Parameters.AddWithValue("form",           product.Form);
        cmd.Parameters.AddWithValue("ingredients",    product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition",      product.NutritionText);
        cmd.Parameters.AddWithValue("calories",       product.CaloriesText as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("protein",        product.ProteinPct   as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat",            product.FatPct       as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber",          product.FiberPct     as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scannedAt",      DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void TruncateAll()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        Exec(conn, "TRUNCATE Products RESTART IDENTITY;");
        Console.WriteLine("[Cleanup] Products truncated.");
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
