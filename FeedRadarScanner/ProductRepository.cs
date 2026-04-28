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

        foreach (var (col, def) in new[]
        {
            ("BrandSlug",    "TEXT"),
            ("TypeSlug",     "TEXT"),
            ("FormSlug",     "TEXT"),
            ("AgeSlug",      "TEXT"),
            ("Volume",       "TEXT"),
            ("Price",        "INTEGER"),
            ("ImageUrl",     "TEXT"),
            ("PhosphorusMg", "DOUBLE PRECISION"),
            ("MoisturePct",  "DOUBLE PRECISION"),
        })
            Exec(conn, $"ALTER TABLE Products ADD COLUMN IF NOT EXISTS {col} {def};");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Brands (
                Slug  TEXT PRIMARY KEY,
                Label TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS Flavors (
                Slug  TEXT PRIMARY KEY,
                Label TEXT NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductFlavors (
                ProductId   INTEGER NOT NULL,
                FlavorSlug  TEXT    NOT NULL,
                PRIMARY KEY (ProductId, FlavorSlug)
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductFunctional (
                ProductId      INTEGER NOT NULL,
                FunctionalSlug TEXT    NOT NULL,
                PRIMARY KEY (ProductId, FunctionalSlug)
            );
            """);

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS ProductSpecial (
                ProductId   INTEGER NOT NULL,
                SpecialSlug TEXT    NOT NULL,
                PRIMARY KEY (ProductId, SpecialSlug)
            );
            """);

        SeedLookups(conn);
    }

    private static void SeedLookups(NpgsqlConnection conn)
    {
        var brands = new[]
        {
            ("wangmiao",  "汪喵星球"), ("alleycat",  "巷弄貓"),
            ("nutrience", "紐崔斯"),   ("ziwi",      "巔峰"),
            ("ziwipeak",  "Ziwi"),     ("schesir",   "Schesir"),
            ("almo",      "Almo Nature"), ("applaws", "Applaws"),
            ("weruva",    "Weruva"),   ("tikicat",   "Tiki Cat"),
            ("cesar",     "西莎"),     ("hills",     "Hill's"),
        };
        foreach (var (slug, label) in brands)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Brands(Slug,Label) VALUES(@s,@l) ON CONFLICT DO NOTHING;";
            cmd.Parameters.AddWithValue("s", slug);
            cmd.Parameters.AddWithValue("l", label);
            cmd.ExecuteNonQuery();
        }

        var flavors = new[]
        {
            ("chicken","雞肉"), ("beef","牛肉"), ("fish","魚肉"),
            ("tuna","鮪魚"),   ("turkey","火雞"), ("lamb","羊肉"),
            ("duck","鴨肉"),   ("salmon","鮭魚"), ("venison","鹿肉"),
            ("rabbit","兔肉"), ("quail","鵪鶉"),  ("mixed","綜合"),
        };
        foreach (var (slug, label) in flavors)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Flavors(Slug,Label) VALUES(@s,@l) ON CONFLICT DO NOTHING;";
            cmd.Parameters.AddWithValue("s", slug);
            cmd.Parameters.AddWithValue("l", label);
            cmd.ExecuteNonQuery();
        }
    }

    public void Upsert(Product product)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        // 1. Upsert product row, returning its Id
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Products (
                Url, Title, IngredientsText, NutritionText, CaloriesText,
                ProteinPct, FatPct, FiberPct, MoisturePct, PhosphorusMg, ScannedAt,
                BrandSlug, TypeSlug, FormSlug, AgeSlug, Volume, Price, ImageUrl
            )
            VALUES (
                @url, @title, @ingredients, @nutrition, @calories,
                @protein, @fat, @fiber, @moisture, @phospho, @scannedAt,
                @brand, @type, @form, @age, @volume, @price, @image
            )
            ON CONFLICT (Url) DO UPDATE SET
                Title           = EXCLUDED.Title,
                IngredientsText = EXCLUDED.IngredientsText,
                NutritionText   = EXCLUDED.NutritionText,
                CaloriesText    = EXCLUDED.CaloriesText,
                ProteinPct      = EXCLUDED.ProteinPct,
                FatPct          = EXCLUDED.FatPct,
                FiberPct        = EXCLUDED.FiberPct,
                MoisturePct     = EXCLUDED.MoisturePct,
                PhosphorusMg    = EXCLUDED.PhosphorusMg,
                ScannedAt       = EXCLUDED.ScannedAt,
                BrandSlug       = EXCLUDED.BrandSlug,
                TypeSlug        = EXCLUDED.TypeSlug,
                FormSlug        = EXCLUDED.FormSlug,
                AgeSlug         = EXCLUDED.AgeSlug,
                Volume          = EXCLUDED.Volume,
                Price           = EXCLUDED.Price,
                ImageUrl        = EXCLUDED.ImageUrl
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("url",        product.Url);
        cmd.Parameters.AddWithValue("title",      product.Title);
        cmd.Parameters.AddWithValue("ingredients", product.IngredientsText);
        cmd.Parameters.AddWithValue("nutrition",  product.NutritionText);
        cmd.Parameters.AddWithValue("calories",   product.CaloriesText   as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("protein",    product.ProteinPct     as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fat",        product.FatPct         as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fiber",      product.FiberPct       as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("moisture",   product.MoisturePct    as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("phospho",    product.PhosphorusMg   as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("scannedAt",  DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("brand",      product.BrandSlug      as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("type",       product.TypeSlug       as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("form",       product.FormSlug       as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("age",        product.AgeSlug        as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("volume",     product.Volume         as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("price",      product.Price          as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("image",      product.ImageUrl       as object ?? DBNull.Value);
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

        // 4. Replace flavor / functional / special join rows
        UpsertSlugs(conn, productId, "ProductFlavors",    "FlavorSlug",    product.FlavorSlugs);
        UpsertSlugs(conn, productId, "ProductFunctional", "FunctionalSlug", product.FunctionalSlugs);
        UpsertSlugs(conn, productId, "ProductSpecial",    "SpecialSlug",   product.SpecialSlugs);

        // 5. Ensure unknown brand/flavor slugs exist in their lookup tables
        if (product.BrandSlug is not null)
            EnsureSlug(conn, "Brands", product.BrandSlug, product.BrandSlug);
        foreach (var fs in product.FlavorSlugs)
            EnsureSlug(conn, "Flavors", fs, fs);
    }

    private static void UpsertSlugs(NpgsqlConnection conn, int productId, string table, string col, List<string> slugs)
    {
        using var del = conn.CreateCommand();
        del.CommandText = $"DELETE FROM {table} WHERE ProductId=@pid;";
        del.Parameters.AddWithValue("pid", productId);
        del.ExecuteNonQuery();

        foreach (var slug in slugs)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = $"INSERT INTO {table}(ProductId,{col}) VALUES(@pid,@slug) ON CONFLICT DO NOTHING;";
            ins.Parameters.AddWithValue("pid",  productId);
            ins.Parameters.AddWithValue("slug", slug);
            ins.ExecuteNonQuery();
        }
    }

    private static void EnsureSlug(NpgsqlConnection conn, string table, string slug, string fallbackLabel)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table}(Slug,Label) VALUES(@s,@l) ON CONFLICT DO NOTHING;";
        cmd.Parameters.AddWithValue("s", slug);
        cmd.Parameters.AddWithValue("l", fallbackLabel);
        cmd.ExecuteNonQuery();
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
