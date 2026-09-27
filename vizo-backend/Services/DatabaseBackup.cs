using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace vizo_backend.Services;

/// <summary>
/// A LOGICAL backup of the whole database, as one .zip.
///
/// ─────────────────────────── WHAT GOES IN THE FILE ─────────────────────────
///
///   &lt;Table&gt;.csv      every base table in the "public" schema, streamed with
///                     COPY "Table" TO STDOUT (FORMAT csv, HEADER) -- the
///                     database's own exporter, so every type (numeric, bytea,
///                     json, timestamps) comes out exactly as it restores.
///   sequences.csv     each sequence and its current value, so ids carry on
///                     from where they were after a restore.
///   manifest.json     when, which database, the server version, the migration
///                     level detected, each table's row count, and the order to
///                     load the tables in so foreign keys are satisfied.
///   RESTORE.txt       how the owner loads it back (see <see cref="RestoreText"/>).
///
/// ─────────────────────────── WHY NOT pg_dump ───────────────────────────────
///
/// The API runs on a host with no PostgreSQL client tools, and shelling out
/// from a web request is its own trouble. COPY is the same engine pg_dump uses
/// for data, reached through the Npgsql driver the API already has. What this
/// does NOT carry is the schema DDL -- the schema is the numbered migration
/// scripts in backend/database, and the manifest records how far along them
/// this database was, which is what a restore needs.
///
/// ─────────────────────────── ONE SNAPSHOT ──────────────────────────────────
///
/// Everything is read inside ONE read-only REPEATABLE READ transaction, so the
/// counts and every CSV describe the same instant: an order taken while the
/// backup runs is either wholly in the file or wholly not, and never an order
/// without its lines.
///
/// "BackupFile" is left out on purpose -- it holds the earlier backups, and
/// including it would put every previous zip inside each new one.
/// </summary>
public static class DatabaseBackup
{
    /// <summary>Tables never copied into a backup, and why (written into the manifest).</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>
    {
        ["BackupFile"] = "holds the earlier backup files themselves",
    };

    /// <summary>
    /// The newest migration whose mark is present, checked newest first. A
    /// PROBE rather than a number written down somewhere: the three sessions of
    /// 27 Sep each add migrations, and a stored "level" that one of them forgot
    /// to bump would be a figure the manifest made up. Each line is a column or
    /// a table the migration created. Add a line when a migration adds one.
    /// </summary>
    private static readonly (int Migration, string Sql)[] Probes =
    {
        (38, Table("BackupFile")),
        (37, Column("User", "MustChangePassword")),
        (36, @"SELECT EXISTS (SELECT 1 FROM ""DocumentSeries"" WHERE ""Prefix"" = 'COL')"),
        (35, Table("ExpenseSheet")),
        (32, Table("LedgerEntryItem")),
        (31, Table("StaffMember")),
        (26, Column("Product", "FsPrice")),
        (25, Column("SalesOrderItem", "DispatchedQty")),
        (22, Column("Party", "CnicFrontUrl")),
        (21, Column("PaymentMethod", "IsForReceiving")),
    };

    private static string Table(string t) =>
        $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{t}')";

    private static string Column(string t, string c) =>
        $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{t}' AND column_name = '{c}')";

    public sealed record Result(byte[] Zip, string Sha256, int TableCount, long RowTotal, int? MigrationLevel);

    /// <summary>Builds the zip in memory and returns it with its counts. Throws on any failure.</summary>
    public static async Task<Result> CreateAsync(string connectionString, DateTime takenAt, string takenBy,
                                                 CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await Exec(conn, tx, "SET TRANSACTION READ ONLY", ct);

        /* ── what is there ── */
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                if (!Excluded.ContainsKey(name)) tables.Add(name);
            }

        var order = await LoadOrder(conn, tx, tables, ct);

        int? level = null;
        foreach (var (migration, sql) in Probes)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            if (await cmd.ExecuteScalarAsync(ct) is true) { level = migration; break; }
        }

        /* ── the zip ── */
        using var buffer = new MemoryStream();
        var counts = new List<object>();
        long rowTotal = 0;

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var table in tables)
            {
                var quoted = Quote(table);

                long rows;
                await using (var cmd = new NpgsqlCommand($"SELECT count(*) FROM {quoted}", conn, tx))
                    rows = (long)(await cmd.ExecuteScalarAsync(ct))!;
                rowTotal += rows;

                var entry = zip.CreateEntry($"{table}.csv", CompressionLevel.Optimal);
                await using (var target = entry.Open())
                await using (var writer = new StreamWriter(target, new UTF8Encoding(false)))
                using (var reader = await conn.BeginTextExportAsync(
                           $"COPY {quoted} TO STDOUT (FORMAT csv, HEADER)", ct))
                {
                    var chunk = new char[64 * 1024];
                    int n;
                    while ((n = await reader.ReadAsync(chunk, 0, chunk.Length)) > 0)
                        await writer.WriteAsync(chunk, 0, n);
                }

                counts.Add(new { table, rows, file = $"{table}.csv" });
            }

            /* Sequences: pg_sequences.last_value is null for one never used. */
            var seq = new StringBuilder("sequence,last_value\n");
            await using (var cmd = new NpgsqlCommand(
                "SELECT sequencename, last_value FROM pg_sequences WHERE schemaname = 'public' ORDER BY sequencename", conn, tx))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    seq.Append(r.GetString(0)).Append(',')
                       .Append(r.IsDBNull(1) ? "" : r.GetInt64(1).ToString()).Append('\n');
            await WriteText(zip, "sequences.csv", seq.ToString());

            var manifest = new
            {
                format = "advpos-logical-backup/1",
                createdAt = takenAt.ToString("yyyy-MM-dd HH:mm:ss") + " (Pakistan time)",
                createdBy = takenBy,
                database = conn.Database,
                serverVersion = conn.PostgreSqlVersion.ToString(),
                migrationLevel = level,
                migrationLevelNote = "The newest migration in backend/database whose table or column is present " +
                                     "(checked, not stored). Build the empty database up to this migration before loading.",
                tableCount = tables.Count,
                rowTotal,
                tables = counts,
                loadOrder = order.Order,
                tablesInForeignKeyCycles = order.Cyclic,
                excluded = Excluded.Select(e => new { table = e.Key, why = e.Value }),
                csv = "COPY ... (FORMAT csv, HEADER), UTF-8, NULL as an empty unquoted field",
            };
            await WriteText(zip, "manifest.json",
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            await WriteText(zip, "RESTORE.txt", RestoreText(level));
        }

        await tx.CommitAsync(ct);

        var bytes = buffer.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new Result(bytes, sha, tables.Count, rowTotal, level);
    }

    /// <summary>
    /// Parents before children, from the foreign keys in pg_constraint, so the
    /// CSVs can be loaded in this order with every constraint switched on.
    /// Self-references do not count (a row may point at an earlier row of its
    /// own table, and COPY loads in file order). Tables caught in a cycle are
    /// listed separately: those need the constraints deferred or dropped for
    /// the load -- RESTORE.txt says how.
    /// </summary>
    private static async Task<(List<string> Order, List<string> Cyclic)> LoadOrder(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<string> tables, CancellationToken ct)
    {
        var deps = tables.ToDictionary(t => t, _ => new HashSet<string>());
        await using (var cmd = new NpgsqlCommand(@"
            SELECT c.relname, p.relname
              FROM pg_constraint k
              JOIN pg_class c ON c.oid = k.conrelid
              JOIN pg_class p ON p.oid = k.confrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE k.contype = 'f' AND n.nspname = 'public'", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var child = r.GetString(0);
                var parent = r.GetString(1);
                if (child != parent && deps.ContainsKey(child) && deps.ContainsKey(parent))
                    deps[child].Add(parent);
            }

        /* Tarjan's strongly connected components. A plain "parents first" pass
           stalls at the first cycle (User.PrimaryLocationId -> Location and
           Location.InChargeUserId -> User is one) and would then call every
           table downstream of it "cyclic" too. Tarjan emits components with
           their dependencies first -- exactly a load order -- and only a
           component of two or more tables is a real cycle. */
        var index = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var order = new List<string>();
        var cyclic = new List<string>();
        var next = 0;

        void Visit(string t)
        {
            index[t] = low[t] = next++;
            stack.Push(t);
            onStack.Add(t);
            foreach (var parent in deps[t].OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!index.ContainsKey(parent)) { Visit(parent); low[t] = Math.Min(low[t], low[parent]); }
                else if (onStack.Contains(parent)) low[t] = Math.Min(low[t], index[parent]);
            }
            if (low[t] != index[t]) return;

            var component = new List<string>();
            string member;
            do { member = stack.Pop(); onStack.Remove(member); component.Add(member); } while (member != t);
            component.Sort(StringComparer.Ordinal);
            order.AddRange(component);
            if (component.Count > 1) cyclic.AddRange(component);
        }

        foreach (var t in tables) if (!index.ContainsKey(t)) Visit(t);
        return (order, cyclic);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await using var w = new StreamWriter(s, new UTF8Encoding(false));
        await w.WriteAsync(text);
    }

    /// <summary>
    /// The restore procedure, inside the file so it travels with it. Restoring
    /// is deliberately NOT a button in the app: it replaces every row in the
    /// live database, and that is a decision for the owner at a desk with the
    /// file in hand, not a click in a browser.
    /// </summary>
    private static string RestoreText(int? level) => $@"AdvPOS logical backup -- how to restore it
==========================================

This zip holds DATA ONLY: one CSV per table (COPY ... FORMAT csv, HEADER),
sequences.csv and manifest.json. The SCHEMA is the numbered scripts in
backend/database of the vizo-backend repository.

Restoring replaces everything. Do it into a NEW, EMPTY database first, check
it, and only then point the API at it. Never load it over the live database.

1. Create an empty database and build the schema up to migration {(level?.ToString() ?? "(see manifest.json)")}:
     01_schema.sql, then every numbered migration in order up to that one.
   Some of those scripts insert seed rows. Empty every table before loading:
     TRUNCATE <every table in manifest.json ""loadOrder""> RESTART IDENTITY CASCADE;

2. Load the tables in the order given by manifest.json ""loadOrder"" (parents
   before children, so foreign keys are satisfied), e.g. with psql:
     \copy ""Role"" FROM 'Role.csv' WITH (FORMAT csv, HEADER)
   The tables in manifest.json ""tablesInForeignKeyCycles"" point at each
   other (User.PrimaryLocationId -> Location, Location.InChargeUserId -> User),
   so neither can go first with every foreign key checked. As a superuser:
     SET session_replication_role = replica;   (load those tables)
     SET session_replication_role = origin;
   Without superuser rights (Neon's owner role): load them into staging copies
   (CREATE TABLE s_x (LIKE ""x"")), insert one with its pointing column set to
   NULL, insert the other, then UPDATE the first from its staging copy.
   (This order was proved on a scratch copy: every table's rows and content
   came back identical.)

3. Put the id counters back where they were, for each line of sequences.csv:
     SELECT setval('""<sequence>""', <last_value>);
   (or run 03_sequence_reset.sql, which winds every sequence past its data.)

4. Compare each table's row count with manifest.json ""tables"" before use.
";
}
