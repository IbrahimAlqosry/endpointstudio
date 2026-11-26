using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Stackwright.Server.Models;

namespace Stackwright.Server.Services.Db;

/// <summary>Database Studio: browse a database's design, its data, and change both.</summary>
public partial class DbStudioService(DatabaseService db)
{
    private const int MaxResultRows = 1000;
    private const int MaxExportRows = 200_000;

    // ------------------------------------------------------------------ Saved connections (passwords encrypted for this Windows user)

    private sealed record StoredConnection(string Id, string Name, DbConnectionInfo Info, string? Secret, DateTime Saved);
    private static readonly string StorePath = Path.Combine(AppStorage.Root, "connections.json");
    private static readonly Lock StoreLock = new();

    private static List<StoredConnection> Load()
    {
        lock (StoreLock)
        {
            try { return File.Exists(StorePath) ? JsonSerializer.Deserialize<List<StoredConnection>>(File.ReadAllText(StorePath), AppStorage.Json) ?? [] : []; }
            catch { return []; }
        }
    }

    private static void Store(List<StoredConnection> list)
    {
        lock (StoreLock)
        {
            Directory.CreateDirectory(AppStorage.Root);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, AppStorage.Json));
            File.Move(tmp, StorePath, true);
        }
    }

    private static string? Protect(string? password)
    {
        if (string.IsNullOrEmpty(password) || !OperatingSystem.IsWindows()) return null;
        return System.Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    }

    private static string? Unprotect(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(System.Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public List<SavedConnection> Connections() =>
        Load().OrderByDescending(c => c.Saved).Select(c => new SavedConnection(c.Id, c.Name, c.Info with { Password = null }, c.Secret is not null, c.Saved)).ToList();

    public SavedConnection SaveConnection(SaveConnectionRequest req)
    {
        var list = Load();
        var existing = req.Id is null ? null : list.FirstOrDefault(c => c.Id == req.Id);
        // An empty password on an existing connection keeps the one already saved
        var secret = !string.IsNullOrEmpty(req.Info.Password) ? Protect(req.Info.Password) : req.Info.IntegratedSecurity ? null : existing?.Secret;
        var name = string.IsNullOrWhiteSpace(req.Name) ? DefaultName(req.Info) : req.Name.Trim();
        var stored = new StoredConnection(existing?.Id ?? Guid.NewGuid().ToString("n")[..12], name, req.Info with { Password = null }, secret, DateTime.UtcNow);
        list.RemoveAll(c => c.Id == stored.Id);
        list.Add(stored);
        Store(list);
        return new SavedConnection(stored.Id, stored.Name, stored.Info, secret is not null, stored.Saved);
    }

    public void DeleteConnection(string id)
    {
        var list = Load();
        list.RemoveAll(c => c.Id == id);
        Store(list);
    }

    private static string DefaultName(DbConnectionInfo i) => i.Provider == DbProvider.Sqlite ? Path.GetFileName(i.Server) : $"{i.Database ?? i.ServiceName ?? i.Provider.ToString()} on {i.Server}";

    public DbConnectionInfo Resolve(DbRef r)
    {
        DbConnectionInfo info;
        if (!string.IsNullOrEmpty(r.ConnectionId))
        {
            var c = Load().FirstOrDefault(x => x.Id == r.ConnectionId) ?? throw new StudioException("That saved connection no longer exists.");
            info = c.Info with { Password = Unprotect(c.Secret) };
            // The browser may send a password for a connection saved without one
            if (info.Password is null && !string.IsNullOrEmpty(r.Connection?.Password)) info = info with { Password = r.Connection.Password };
        }
        else info = r.Connection ?? throw new StudioException("Choose a database first.");
        if (!string.IsNullOrWhiteSpace(r.Database) && info.Provider != DbProvider.Sqlite)
            info = info.Provider == DbProvider.Oracle ? info with { Database = r.Database } : info with { Database = r.Database };
        return info;
    }

    public async Task<object> ConnectAsync(DbRef r, CancellationToken ct)
    {
        var info = Resolve(r);
        var (version, databases) = await db.TestAsync(info, ct);
        return new { version, databases };
    }

    private async Task<(DbConnection Conn, DbConnectionInfo Info)> OpenAsync(DbRef r, CancellationToken ct, bool write = false)
    {
        var info = Resolve(r);
        if (info.Provider == DbProvider.Oracle && string.IsNullOrWhiteSpace(info.Database)) info = info with { Database = info.Username };
        var conn = await db.OpenAsync(info, ct, write);
        if (info.Provider == DbProvider.Sqlite && write)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return (conn, info);
    }

    private static async Task<T> Guard<T>(Func<Task<T>> work)
    {
        try { return await work(); }
        catch (DbException ex) { throw new StudioException(Friendly(ex)); }
        catch (InvalidOperationException ex) { throw new StudioException(ex.Message); }
    }

    private static string Friendly(DbException ex)
    {
        var m = ex.Message.Trim();
        if (Regex.IsMatch(m, "REFERENCE constraint|foreign key constraint|ORA-02292|FOREIGN KEY constraint failed|violates foreign key", RegexOptions.IgnoreCase))
            return "Other rows depend on this one (a relation points at it), so it can't be removed or changed. Details: " + m;
        if (Regex.IsMatch(m, "duplicate key|UNIQUE constraint|ORA-00001|Duplicate entry|violates unique", RegexOptions.IgnoreCase))
            return "Another row already has that value, and it must be unique. Details: " + m;
        if (Regex.IsMatch(m, "cannot insert the value NULL|ORA-01400|NOT NULL constraint|violates not-null|cannot be null", RegexOptions.IgnoreCase))
            return "A required value is empty. Details: " + m;
        return m;
    }

    // ------------------------------------------------------------------ Design

    public Task<DbSchema> SchemaAsync(DbRef r, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(r, ct);
        await using (conn) return await SchemaReader.ReadAsync(conn, info, ct);
    });

    public Task<string> RoutineAsync(RoutineRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn) return await SchemaReader.RoutineDefinitionAsync(conn, info, req.Schema, req.Name, req.Kind, ct);
    });

    public Task<string> ScriptAsync(DbRef r, string schema, string table, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(r, ct);
        await using (conn)
        {
            var s = await SchemaReader.ReadAsync(conn, info, ct);
            var t = s.Tables.FirstOrDefault(x => x.Schema == schema && x.Name == table) ?? throw new StudioException("That table no longer exists.");
            return SqlDialect.For(info.Provider).Script(t, s);
        }
    });

    public Task<DdlPreview> PreviewAsync(DdlRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn)
        {
            var s = await SchemaReader.ReadAsync(conn, info, ct);
            var statements = SqlDialect.For(info.Provider).Ddl(req.Change, s);
            return new DdlPreview(statements, Warning(req.Change, s));
        }
    });

    private static string? Warning(DdlChange ch, DbSchema s)
    {
        var t = s.Tables.FirstOrDefault(x => x.Name == ch.Table && x.Schema == (ch.Schema ?? ""));
        var rows = t?.Rows ?? 0;
        var deps = s.ForeignKeys.Where(f => f.RefTable == ch.Table && f.RefSchema == (ch.Schema ?? "") && f.Table != ch.Table).Select(f => f.Table).Distinct().ToList();
        return ch.Kind switch
        {
            "dropTable" => $"This deletes the table and all its {rows:N0} rows for good." + (deps.Count > 0 ? $" These tables point at it and must lose that relation first: {string.Join(", ", deps)}." : ""),
            "truncateTable" => $"This removes all {rows:N0} rows from the table." + (deps.Count > 0 ? $" Tables that point at it: {string.Join(", ", deps)}." : ""),
            "dropColumn" => "This deletes the column and every value stored in it.",
            "alterColumn" => rows > 0 ? "Existing values will be converted. If any value doesn't fit the new type, nothing is changed and you'll see why." : null,
            "dropView" or "dropRoutine" => "Anything that uses it will stop working.",
            "renameTable" or "renameColumn" => "Code, views and procedures that use the old name will need updating.",
            _ => null,
        };
    }

    public Task<object> ApplyDdlAsync(DdlRequest req, CancellationToken ct) => Guard<object>(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            var s = await SchemaReader.ReadAsync(conn, info, ct);
            var statements = SqlDialect.For(info.Provider).Ddl(req.Change, s);
            // SQL Server, PostgreSQL and SQLite can undo schema changes when one step fails; MySQL and Oracle commit each one.
            var transactional = info.Provider is DbProvider.SqlServer or DbProvider.PostgreSql or DbProvider.Sqlite;
            await using var tx = transactional ? await conn.BeginTransactionAsync(ct) : null;
            foreach (var sql in statements)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (tx is not null) await tx.CommitAsync(ct);
            return new { statements };
        }
    });

    // ------------------------------------------------------------------ Data

    private static async Task<DbsTable> TableAsync(DbConnection conn, DbConnectionInfo info, string schema, string table, CancellationToken ct)
    {
        var s = await SchemaReader.ReadAsync(conn, info, ct);
        return s.Tables.FirstOrDefault(x => x.Schema == schema && x.Name == table)
            ?? (s.Views.Any(v => v.Schema == schema && v.Name == table) ? await ViewAsTable(conn, info, schema, table, ct) : null)
            ?? throw new StudioException("That table no longer exists.");
    }

    private static async Task<DbsTable> ViewAsTable(DbConnection conn, DbConnectionInfo info, string schema, string name, CancellationToken ct)
    {
        var d = SqlDialect.For(info.Provider);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = d.Page($"SELECT * FROM {d.Table(schema, name)}", "1", 0, 1);
        if (info.Provider == DbProvider.SqlServer) cmd.CommandText = $"SELECT TOP 0 * FROM {d.Table(schema, name)}";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, r.FieldCount).Select(i => new DbsColumn(r.GetName(i), r.GetDataTypeName(i), true, null, false, false, true, i + 1, null)).ToList();
        return new DbsTable(schema, name, null, cols, [], null);
    }

    private static bool Searchable(DbsColumn c) => !Regex.IsMatch(c.Type, @"blob|binary|bytea|image|raw|xml|geography|geometry|hierarchyid|sql_variant|long|clob|ntext|^text$", RegexOptions.IgnoreCase);

    private static (string Where, List<(string, object)> Params) Where(SqlDialect d, DbsTable t, string? search, Dictionary<string, string?>? filters, DbProvider p)
    {
        var parts = new List<string>();
        var ps = new List<(string, object)>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var cols = t.Columns.Where(Searchable).ToList();
            if (p is DbProvider.PostgreSql or DbProvider.Sqlite or DbProvider.MySql)
                cols = t.Columns.Where(c => !Regex.IsMatch(c.Type, "blob|binary|bytea", RegexOptions.IgnoreCase)).ToList();
            if (cols.Count > 0)
            {
                ps.Add((d.Param(ps.Count), "%" + search.Trim().ToLowerInvariant() + "%"));
                var pn = d.Param(ps.Count - 1);
                parts.Add("(" + string.Join(" OR ", cols.Select(c => $"{d.Lower(d.AsText(d.Quote(c.Name)))} {d.Like} {pn}")) + ")");
            }
        }
        foreach (var (col, value) in filters ?? [])
        {
            if (t.Columns.All(c => c.Name != col) || value is null) continue;
            var q = d.Quote(col);
            var v = value.Trim();
            if (v.Equals("(empty)", StringComparison.OrdinalIgnoreCase) || v.Equals("null", StringComparison.OrdinalIgnoreCase)) { parts.Add($"{q} IS NULL"); continue; }
            if (v.Equals("(not empty)", StringComparison.OrdinalIgnoreCase)) { parts.Add($"{q} IS NOT NULL"); continue; }
            var m = Regex.Match(v, @"^(>=|<=|<>|!=|=|>|<)\s*(.+)$");
            if (m.Success)
            {
                var column = t.Columns.First(c => c.Name == col);
                ps.Add((d.Param(ps.Count), Convert(column, m.Groups[2].Value, p) ?? DBNull.Value));
                parts.Add($"{q} {(m.Groups[1].Value == "!=" ? "<>" : m.Groups[1].Value)} {d.Param(ps.Count - 1)}");
                continue;
            }
            ps.Add((d.Param(ps.Count), "%" + v.ToLowerInvariant() + "%"));
            parts.Add($"{d.Lower(d.AsText(q))} {d.Like} {d.Param(ps.Count - 1)}");
        }
        return (parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts), ps);
    }

    private static DbCommand Command(DbConnection conn, string sql, IEnumerable<(string, object)> ps, DbTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.CommandTimeout = 120;
        if (cmd is OracleCommand oc) { oc.BindByName = true; oc.InitialLONGFetchSize = -1; oc.InitialLOBFetchSize = -1; }
        foreach (var (n, v) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n.TrimStart(':');
            p.Value = v;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    public Task<RowsResult> RowsAsync(RowsRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn)
        {
            var d = SqlDialect.For(info.Provider);
            var t = await TableAsync(conn, info, req.Schema, req.Table, ct);
            var (where, ps) = Where(d, t, req.Search, req.Filters, info.Provider);
            var from = d.Table(t.Schema, t.Name);
            long total;
            await using (var cmd = Command(conn, $"SELECT COUNT(*) FROM {from}{where}", ps)) total = System.Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

            var orderCol = req.OrderBy is { } ob && t.Columns.Any(c => c.Name == ob) ? d.Quote(ob) + (req.Desc ? " DESC" : "")
                : t.PrimaryKey.Count > 0 ? string.Join(", ", t.PrimaryKey.Select(d.Quote))
                : info.Provider == DbProvider.SqlServer ? "(SELECT NULL)" : "1";
            if (req.OrderBy is not null && t.PrimaryKey.Count > 0 && !t.PrimaryKey.Contains(req.OrderBy)) orderCol += ", " + string.Join(", ", t.PrimaryKey.Select(d.Quote));
            var limit = Math.Clamp(req.Limit, 1, 1000);
            var cols = string.Join(", ", t.Columns.Select(c => d.Quote(c.Name)));
            var sql = d.Page($"SELECT {cols} FROM {from}{where}", orderCol, Math.Max(0, req.Offset), limit);
            var rows = new List<List<string?>>();
            await using (var cmd = Command(conn, sql, ps))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => Text(r, i)).ToList());
            var keys = t.PrimaryKey.Count > 0 ? t.PrimaryKey : t.Columns.Where(c => Searchable(c) && !c.Computed).Select(c => c.Name).ToList();
            var readOnly = t.Rows is null && t.PrimaryKey.Count == 0 && t.Columns.All(c => c.Computed); // views
            return new RowsResult(t.Columns.Select(c => c.Name).ToList(), rows, total, keys, readOnly);
        }
    });

    private static string? Text(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        object v;
        try { v = r.GetValue(i); }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException) { return r.GetProviderSpecificValue(i)?.ToString(); }
        return Format(v);
    }

    private static string? Format(object? v) => v switch
    {
        null or DBNull => null,
        byte[] b => b.Length == 16 ? new Guid(b).ToString() + "  (0x" + System.Convert.ToHexString(b) + ")" : "0x" + System.Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 64))) + (b.Length > 64 ? $"… ({b.Length:N0} bytes)" : ""),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString(dt.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Array a => JsonSerializer.Serialize(a.Cast<object?>().Select(Format)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>Turns text typed in the grid into the value the column expects.</summary>
    private static object? Convert(DbsColumn c, string? text, DbProvider p)
    {
        if (text is null) return null;
        var t = c.Type.ToLowerInvariant();
        var s = text.Trim();
        try
        {
            if (Regex.IsMatch(t, @"^(bit|bool|boolean)$") || t == "tinyint(1)")
                return s.ToLowerInvariant() switch { "1" or "true" or "yes" or "y" => p == DbProvider.MySql ? 1 : true, "0" or "false" or "no" or "n" => p == DbProvider.MySql ? 0 : false, _ => throw new FormatException() };
            if (Regex.IsMatch(t, @"^(int|integer|int4|serial)\b|^mediumint")) return int.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^(bigint|int8|bigserial)\b")) return long.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^(smallint|int2|smallserial)\b")) return short.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^tinyint\b")) return p == DbProvider.SqlServer ? byte.Parse(s, CultureInfo.InvariantCulture) : (object)sbyte.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^(decimal|numeric|money|smallmoney|number)")) return decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^(float|double|real|binary_double|binary_float)")) return double.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^(uuid|uniqueidentifier)$")) return Guid.Parse(s);
            if (Regex.IsMatch(t, @"^date$") && p == DbProvider.PostgreSql) return DateOnly.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^time( without time zone)?$") && p == DbProvider.PostgreSql) return TimeOnly.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^time(\(\d\))?$") && p is DbProvider.SqlServer or DbProvider.MySql) return TimeSpan.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"datetimeoffset|timestamptz|timestamp with time zone")) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"^(date|datetime|datetime2|smalldatetime|timestamp)") && p != DbProvider.Sqlite) return DateTime.Parse(s, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(t, @"binary|blob|bytea|image|^raw") && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return System.Convert.FromHexString(s[2..]);
            if (p == DbProvider.PostgreSql && t.StartsWith('_'))
            {
                // PostgreSQL arrays: accept ["a","b"] or a,b
                var items = s.StartsWith('[') ? JsonSerializer.Deserialize<List<JsonElement>>(s)!.Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString()).ToList()
                    : s.Trim('{', '}').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
                return t switch
                {
                    "_int4" or "_int2" => items.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
                    "_int8" => items.Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
                    "_numeric" => (object)items.Select(x => decimal.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
                    "_float8" or "_float4" => items.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(),
                    "_bool" => items.Select(x => x is "true" or "t" or "1").ToArray(),
                    "_uuid" => items.Select(Guid.Parse).ToArray(),
                    _ => items.ToArray(),
                };
            }
            if (p == DbProvider.PostgreSql && t is "json" or "jsonb")
                return new Npgsql.NpgsqlParameter { Value = text, NpgsqlDbType = t == "json" ? NpgsqlTypes.NpgsqlDbType.Json : NpgsqlTypes.NpgsqlDbType.Jsonb };
            return text;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or JsonException)
        {
            throw new StudioException($"“{text}” isn't a valid value for {c.Name} ({c.Type}).");
        }
    }

    private static void AddValue(DbCommand cmd, string name, object? value)
    {
        name = name.TrimStart(':');
        if (value is DbParameter np) { np.ParameterName = name; cmd.Parameters.Add(np); return; }
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static (string Sql, List<(string Name, object? Value)> Ps) KeyMatch(SqlDialect d, DbsTable t, Dictionary<string, string?> keys, DbProvider p, int start)
    {
        var parts = new List<string>();
        var ps = new List<(string, object?)>();
        foreach (var (k, v) in keys)
        {
            var col = t.Columns.FirstOrDefault(c => c.Name == k) ?? throw new StudioException($"The column {k} no longer exists.");
            if (v is null) { parts.Add($"{d.Quote(k)} IS NULL"); continue; }
            var n = d.Param(start + ps.Count);
            ps.Add((n, Convert(col, v, p)));
            parts.Add($"{d.Quote(k)} = {n}");
        }
        if (parts.Count == 0) throw new StudioException("This row can't be identified.");
        return (string.Join(" AND ", parts), ps);
    }

    public Task<object> InsertAsync(RowChange req, CancellationToken ct) => Guard<object>(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            var t = await TableAsync(conn, info, req.Schema, req.Table, ct);
            var n = await InsertRows(conn, null, info.Provider, t, [req.Values ?? []], ct);
            return new { affected = n };
        }
    });

    private static async Task<int> InsertRows(DbConnection conn, DbTransaction? tx, DbProvider p, DbsTable t, List<Dictionary<string, string?>> rows, CancellationToken ct)
    {
        var d = SqlDialect.For(p);
        var total = 0;
        foreach (var values in rows)
        {
            var cols = t.Columns.Where(c => !c.Computed && values.TryGetValue(c.Name, out var v) && !(v is null && (c.Identity || c.Default is not null))).ToList();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (cmd is OracleCommand oc) oc.BindByName = true;
            if (cols.Count == 0)
                cmd.CommandText = p switch
                {
                    DbProvider.MySql => $"INSERT INTO {d.Table(t.Schema, t.Name)} () VALUES ()",
                    DbProvider.Oracle => $"INSERT INTO {d.Table(t.Schema, t.Name)} ({d.Quote(t.Columns[0].Name)}) VALUES (DEFAULT)",
                    _ => $"INSERT INTO {d.Table(t.Schema, t.Name)} DEFAULT VALUES",
                };
            else
            {
                cmd.CommandText = $"INSERT INTO {d.Table(t.Schema, t.Name)} ({string.Join(", ", cols.Select(c => d.Quote(c.Name)))}) VALUES ({string.Join(", ", cols.Select((_, i) => d.Param(i)))})";
                for (var i = 0; i < cols.Count; i++) AddValue(cmd, d.Param(i), Convert(cols[i], values[cols[i].Name], p));
            }
            // SQL Server refuses explicit values for identity columns unless asked
            if (p == DbProvider.SqlServer && cols.Any(c => c.Identity))
                cmd.CommandText = $"SET IDENTITY_INSERT {d.Table(t.Schema, t.Name)} ON; {cmd.CommandText}; SET IDENTITY_INSERT {d.Table(t.Schema, t.Name)} OFF";
            total += await cmd.ExecuteNonQueryAsync(ct);
        }
        return total;
    }

    public Task<object> UpdateAsync(RowChange req, CancellationToken ct) => Guard<object>(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            var d = SqlDialect.For(info.Provider);
            var t = await TableAsync(conn, info, req.Schema, req.Table, ct);
            var values = (req.Values ?? []).Where(v => t.Columns.Any(c => c.Name == v.Key && !c.Computed)).ToList();
            if (values.Count == 0) return new { affected = 0 };
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (cmd is OracleCommand oc) oc.BindByName = true;
            var sets = new List<string>();
            foreach (var (k, v) in values)
            {
                var n = d.Param(sets.Count);
                sets.Add($"{d.Quote(k)} = {n}");
                AddValue(cmd, n, Convert(t.Columns.First(c => c.Name == k), v, info.Provider));
            }
            var (match, ps) = KeyMatch(d, t, req.Keys ?? [], info.Provider, sets.Count);
            foreach (var (n, v) in ps) AddValue(cmd, n, v);
            cmd.CommandText = $"UPDATE {d.Table(t.Schema, t.Name)} SET {string.Join(", ", sets)} WHERE {match}";
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected > 1)
            {
                await tx.RollbackAsync(ct);
                throw new StudioException($"This change would have touched {affected} identical rows, so nothing was saved. Give the table a key to edit rows like this one.");
            }
            await tx.CommitAsync(ct);
            return new { affected };
        }
    });

    public Task<object> DeleteAsync(RowChange req, CancellationToken ct) => Guard<object>(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            var d = SqlDialect.For(info.Provider);
            var t = await TableAsync(conn, info, req.Schema, req.Table, ct);
            var list = req.KeyList ?? (req.Keys is null ? [] : [req.Keys]);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var total = 0;
            foreach (var keys in list)
            {
                var (match, ps) = KeyMatch(d, t, keys, info.Provider, 0);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                if (cmd is OracleCommand oc) oc.BindByName = true;
                cmd.CommandText = $"DELETE FROM {d.Table(t.Schema, t.Name)} WHERE {match}";
                foreach (var (n, v) in ps) AddValue(cmd, n, v);
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected > 1)
                {
                    await tx.RollbackAsync(ct);
                    throw new StudioException($"One of the rows matched {affected} identical rows, so nothing was deleted. Give the table a key to delete rows like this one.");
                }
                total += affected;
            }
            await tx.CommitAsync(ct);
            return new { affected = total };
        }
    });

    public Task<object> ImportAsync(ImportRequest req, CancellationToken ct) => Guard<object>(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            var t = await TableAsync(conn, info, req.Schema, req.Table, ct);
            var unknown = req.Rows.SelectMany(r => r.Keys).Distinct().Where(k => t.Columns.All(c => !c.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
            var rows = req.Rows.Select(r => r.Where(kv => !unknown.Contains(kv.Key))
                .ToDictionary(kv => t.Columns.First(c => c.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Name, kv => string.IsNullOrEmpty(kv.Value) ? null : kv.Value)).ToList();
            await using var tx = await conn.BeginTransactionAsync(ct);
            int n;
            try { n = await InsertRows(conn, tx, info.Provider, t, rows, ct); }
            catch (DbException ex) { await tx.RollbackAsync(ct); throw new StudioException("Nothing was imported. " + Friendly(ex)); }
            catch (StudioException ex) { await tx.RollbackAsync(ct); throw new StudioException("Nothing was imported. " + ex.Message); }
            await tx.CommitAsync(ct);
            return new { affected = n, ignoredColumns = unknown };
        }
    });

    public Task<(string Content, string FileName, string Type)> ExportAsync(ExportRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn)
        {
            var d = SqlDialect.For(info.Provider);
            var t = await TableAsync(conn, info, req.Schema, req.Table, ct);
            var (where, ps) = Where(d, t, req.Search, null, info.Provider);
            var order = t.PrimaryKey.Count > 0 ? string.Join(", ", t.PrimaryKey.Select(d.Quote)) : info.Provider == DbProvider.SqlServer ? "(SELECT NULL)" : "1";
            var sql = d.Page($"SELECT {string.Join(", ", t.Columns.Select(c => d.Quote(c.Name)))} FROM {d.Table(t.Schema, t.Name)}{where}", order, 0, MaxExportRows);
            var names = t.Columns.Select(c => c.Name).ToList();
            var sb = new StringBuilder();
            var format = req.Format.ToLowerInvariant();
            if (format == "csv") sb.AppendLine(string.Join(",", names.Select(Csv)));
            else if (format == "json") sb.Append('[');
            var first = true;
            await using (var cmd = Command(conn, sql, ps))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var values = Enumerable.Range(0, r.FieldCount).Select(i => Text(r, i)).ToList();
                    switch (format)
                    {
                        case "csv": sb.AppendLine(string.Join(",", values.Select(v => v is null ? "" : Csv(v)))); break;
                        case "json":
                            sb.Append(first ? "\n  " : ",\n  ").Append(JsonSerializer.Serialize(names.Zip(values).ToDictionary(x => x.First, x => x.Second)));
                            break;
                        default: // sql inserts
                            sb.Append($"INSERT INTO {d.Table(t.Schema, t.Name)} ({string.Join(", ", names.Select(d.Quote))}) VALUES (")
                              .Append(string.Join(", ", values.Select((v, i) => Literal(v, t.Columns[i])))).AppendLine(");");
                            break;
                    }
                    first = false;
                }
            if (format == "json") sb.Append("\n]\n");
            var ext = format is "csv" or "json" ? format : "sql";
            return (sb.ToString(), $"{t.Name}.{ext}", ext switch { "csv" => "text/csv", "json" => "application/json", _ => "text/plain" });
        }
    });

    private static string Csv(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

    private static string Literal(string? v, DbsColumn c)
    {
        if (v is null) return "NULL";
        if (Regex.IsMatch(c.Type, @"^(int|bigint|smallint|tinyint|decimal|numeric|number|float|double|real|money|integer|bit)", RegexOptions.IgnoreCase) && Regex.IsMatch(v, @"^-?\d+(\.\d+)?$")) return v;
        return "'" + v.Replace("'", "''") + "'";
    }

    // ------------------------------------------------------------------ SQL runner

    [GeneratedRegex(@"^\s*(DROP|TRUNCATE|ALTER)\b|^\s*(DELETE|UPDATE)\b(?![\s\S]*\bWHERE\b)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DangerousRx();

    [GeneratedRegex(@"^\s*(CREATE|ALTER|DROP|TRUNCATE|RENAME|GRANT|REVOKE)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DdlRx();

    public Task<SqlResult> RunSqlAsync(SqlRequest req, CancellationToken ct) => Guard(async () =>
    {
        var sqlText = req.Sql ?? "";
        if (string.IsNullOrWhiteSpace(sqlText)) throw new StudioException("Write a query to run.");
        var statements = Split(sqlText, Resolve(req.Ref).Provider);
        if (!req.Confirmed && !req.DryRun)
        {
            var bad = statements.Select(s => Strip(s)).FirstOrDefault(s => DangerousRx().IsMatch(s));
            if (bad is not null)
                return new SqlResult([], 0, false, $"This can remove or change a lot of data or design: “{Shorten(bad)}”. Run it anyway?");
        }
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            if (req.DryRun && info.Provider is DbProvider.MySql or DbProvider.Oracle && statements.Any(s => DdlRx().IsMatch(Strip(s))))
                throw new StudioException($"{info.Provider} saves design changes immediately, so they can't be tried out safely. Remove them or run for real.");
            var sw = Stopwatch.StartNew();
            var results = new List<SqlResultSet>();
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var statement in statements)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = statement;
                    cmd.CommandTimeout = 600;
                    if (cmd is OracleCommand oc) { oc.InitialLONGFetchSize = -1; oc.InitialLOBFetchSize = -1; }
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    var any = false;
                    do
                    {
                        if (r.FieldCount == 0) continue;
                        any = true;
                        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
                        var rows = new List<List<string?>>();
                        var truncated = false;
                        while (await r.ReadAsync(ct))
                        {
                            if (rows.Count >= MaxResultRows) { truncated = true; break; }
                            rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => Text(r, i)).ToList());
                        }
                        results.Add(new SqlResultSet(cols, rows, truncated, null, null));
                    } while (await r.NextResultAsync(ct));
                    await r.CloseAsync();
                    if (!any) results.Add(new SqlResultSet([], [], false, r.RecordsAffected, Shorten(Strip(statement))));
                }
                if (req.DryRun) await tx.RollbackAsync(ct);
                else await tx.CommitAsync(ct);
            }
            catch (DbException ex)
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { /* already gone */ }
                var done = results.Count > 0 && !(info.Provider is DbProvider.SqlServer or DbProvider.PostgreSql or DbProvider.Sqlite) ? " Statements before it may already be saved." : " Nothing was saved.";
                throw new StudioException(Friendly(ex) + done);
            }
            return new SqlResult(results, sw.ElapsedMilliseconds, req.DryRun);
        }
    });

    private static string Shorten(string s)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        return s.Length > 90 ? s[..90] + "…" : s;
    }

    /// <summary>Removes comments so checks look at the statement itself.</summary>
    private static string Strip(string sql) => Regex.Replace(Regex.Replace(sql, @"/\*[\s\S]*?\*/", " "), @"--[^\n]*", " ").Trim();

    /// <summary>Splits a script into statements the database can run one at a time.</summary>
    public static List<string> Split(string sql, DbProvider p)
    {
        // SQL Server batches are separated by GO lines
        if (p == DbProvider.SqlServer)
            return Regex.Split(sql, @"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline).Where(s => Strip(s).Length > 0).ToList();
        // Oracle PL/SQL blocks end with a '/' line; plain statements end with ';'
        var blocks = p == DbProvider.Oracle ? Regex.Split(sql, @"^\s*/\s*$", RegexOptions.Multiline) : [sql];
        var list = new List<string>();
        foreach (var block in blocks)
        {
            if (p == DbProvider.Oracle && Regex.IsMatch(Strip(block), @"^(BEGIN|DECLARE|CREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE))\b", RegexOptions.IgnoreCase))
            {
                if (Strip(block).Length > 0) list.Add(block.Trim());
                continue;
            }
            var sb = new StringBuilder();
            var dollar = (string?)null;
            for (var i = 0; i < block.Length; i++)
            {
                var c = block[i];
                if (dollar is not null)
                {
                    if (string.CompareOrdinal(block, i, dollar, 0, dollar.Length) == 0) { sb.Append(dollar); i += dollar.Length - 1; dollar = null; }
                    else sb.Append(c);
                    continue;
                }
                if (c is '\'' or '"' or '`')
                {
                    var end = block.IndexOf(c, i + 1);
                    while (end > 0 && end + 1 < block.Length && block[end + 1] == c) end = block.IndexOf(c, end + 2);
                    if (end < 0) end = block.Length - 1;
                    sb.Append(block, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '-' && i + 1 < block.Length && block[i + 1] == '-')
                {
                    var end = block.IndexOf('\n', i);
                    if (end < 0) end = block.Length - 1;
                    sb.Append(block, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '/' && i + 1 < block.Length && block[i + 1] == '*')
                {
                    var end = block.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? block.Length - 1 : end + 1;
                    sb.Append(block, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (c == '$' && p == DbProvider.PostgreSql)
                {
                    var m = Regex.Match(block[i..], @"^\$[A-Za-z_]*\$");
                    if (m.Success) { dollar = m.Value; sb.Append(dollar); i += dollar.Length - 1; continue; }
                }
                if (c == ';')
                {
                    if (Strip(sb.ToString()).Length > 0) list.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }
            if (Strip(sb.ToString()).Length > 0) list.Add(sb.ToString().Trim());
        }
        return list;
    }
}
