using System.Data.Common;
using DbColumn = EndpointStudio.Server.Models.DbColumn;
using EndpointStudio.Server.Models;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace EndpointStudio.Server.Services;

/// <summary>Reads database structure (databases, tables, columns) from SQL Server or MySQL.</summary>
public class DatabaseService
{
    private static readonly HashSet<string> MySqlSystemDbs = new(StringComparer.OrdinalIgnoreCase)
        { "information_schema", "mysql", "performance_schema", "sys" };

    public string ConnectionString(DbConnectionInfo c, bool forApp = false)
    {
        if (string.IsNullOrWhiteSpace(c.Server)) throw new StudioException("Please enter the server name.");
        if (c.Provider == DbProvider.SqlServer)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = c.Port is > 0 ? $"{c.Server.Trim()},{c.Port}" : c.Server.Trim(),
                IntegratedSecurity = c.IntegratedSecurity,
                TrustServerCertificate = true,
                MultipleActiveResultSets = forApp,
            };
            if (!forApp) b.ConnectTimeout = 8;
            if (!c.IntegratedSecurity)
            {
                b.UserID = c.Username ?? "";
                b.Password = c.Password ?? "";
            }
            if (!string.IsNullOrWhiteSpace(c.Database)) b.InitialCatalog = c.Database;
            return b.ConnectionString;
        }
        else
        {
            var b = new MySqlConnectionStringBuilder
            {
                Server = c.Server.Trim(),
                Port = (uint)(c.Port is > 0 ? c.Port.Value : 3306),
                UserID = c.Username ?? "root",
                Password = c.Password ?? "",
            };
            if (!forApp) b.ConnectionTimeout = 8;
            if (!string.IsNullOrWhiteSpace(c.Database)) b.Database = c.Database;
            return b.ConnectionString;
        }
    }

    private async Task<DbConnection> OpenAsync(DbConnectionInfo c, CancellationToken ct)
    {
        DbConnection conn = c.Provider == DbProvider.SqlServer
            ? new SqlConnection(ConnectionString(c))
            : new MySqlConnection(ConnectionString(c));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (Exception ex) when (ex is SqlException or MySqlException or InvalidOperationException)
        {
            await conn.DisposeAsync();
            throw new StudioException("Couldn't connect to the database server. " + FriendlyError(ex));
        }
    }

    private static string FriendlyError(Exception ex)
    {
        var m = ex.Message;
        if (m.Contains("Login failed", StringComparison.OrdinalIgnoreCase) || m.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
            return "The username or password is wrong, or this login isn't allowed.";
        if (m.Contains("network-related", StringComparison.OrdinalIgnoreCase) || m.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase))
            return "The server couldn't be reached. Check the server name and that it is running.";
        return m;
    }

    public async Task<(string Version, List<string> Databases)> TestAsync(DbConnectionInfo c, CancellationToken ct)
    {
        await using var conn = await OpenAsync(c with { Database = null }, ct);
        var version = conn.ServerVersion;
        var list = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = c.Provider == DbProvider.SqlServer
            ? "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name"
            : "SHOW DATABASES";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var n = r.GetString(0);
            if (c.Provider == DbProvider.MySql && MySqlSystemDbs.Contains(n)) continue;
            list.Add(n);
        }
        return (version, list);
    }

    public async Task<string> ServerVersionAsync(DbConnectionInfo c, CancellationToken ct)
    {
        await using var conn = await OpenAsync(c, ct);
        return conn.ServerVersion;
    }

    public async Task<List<DbTable>> GetTablesAsync(DbConnectionInfo c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Database)) throw new StudioException("Please choose a database.");
        await using var conn = await OpenAsync(c, ct);
        return c.Provider == DbProvider.SqlServer ? await SqlServerTables(conn, ct) : await MySqlTables(conn, c.Database!, ct);
    }

    private static async Task<List<DbTable>> SqlServerTables(DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH,
                   CAST(ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity'), 0) AS int) AS IsIdentity,
                   CAST(ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsComputed'), 0) AS int) AS IsComputed,
                   CASE WHEN pk.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IsPk
            FROM INFORMATION_SCHEMA.TABLES t
            JOIN INFORMATION_SCHEMA.COLUMNS c ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME
            LEFT JOIN (
                SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON ku.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND ku.TABLE_SCHEMA = tc.TABLE_SCHEMA
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON pk.TABLE_SCHEMA = c.TABLE_SCHEMA AND pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE t.TABLE_NAME NOT IN ('__EFMigrationsHistory', 'sysdiagrams')
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME, c.ORDINAL_POSITION
            """;
        var tables = await ReadColumns(conn, sql, row => SqlServerType(row.DataType), ct);

        // Approximate row counts (fast, from metadata)
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.name, o.name, SUM(p.rows) FROM sys.partitions p
                JOIN sys.objects o ON o.object_id = p.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE p.index_id IN (0,1) AND o.type = 'U' GROUP BY s.name, o.name
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var counts = new Dictionary<(string, string), long>();
            while (await r.ReadAsync(ct)) counts[(r.GetString(0), r.GetString(1))] = r.GetInt64(2);
            return tables.Select(t => t with { RowCount = counts.TryGetValue((t.Schema, t.Name), out var n) ? n : null }).ToList();
        }
        catch { return tables; }
    }

    private static async Task<List<DbTable>> MySqlTables(DbConnection conn, string db, CancellationToken ct)
    {
        const string sql = """
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH,
                   CASE WHEN c.EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END AS IsIdentity,
                   CASE WHEN c.EXTRA LIKE '%GENERATED%' THEN 1 ELSE 0 END AS IsComputed,
                   CASE WHEN c.COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END AS IsPk,
                   c.COLUMN_TYPE, t.TABLE_ROWS
            FROM information_schema.TABLES t
            JOIN information_schema.COLUMNS c ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME
            WHERE t.TABLE_SCHEMA = @db AND t.TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY t.TABLE_NAME, c.ORDINAL_POSITION
            """;
        return await ReadColumns(conn, sql, row => MySqlType(row.DataType, row.ColumnType ?? ""), ct, ("@db", db));
    }

    private sealed record RawRow(string DataType, string? ColumnType);

    private static async Task<List<DbTable>> ReadColumns(DbConnection conn, string sql, Func<RawRow, string?> map, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v;
            cmd.Parameters.Add(p);
        }
        var tables = new List<DbTable>();
        var index = new Dictionary<(string, string), DbTable>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var hasExtra = r.FieldCount > 10;
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            if (!index.TryGetValue((schema, name), out var table))
            {
                long? rows = hasExtra && !r.IsDBNull(11) ? Convert.ToInt64(r.GetValue(11)) : null;
                table = new DbTable(schema, name, [], r.GetString(2).Contains("VIEW", StringComparison.OrdinalIgnoreCase), rows);
                index[(schema, name)] = table;
                tables.Add(table);
            }
            var dataType = r.GetString(4).ToLowerInvariant();
            var columnType = hasExtra ? r.GetString(10).ToLowerInvariant() : null;
            var clr = map(new RawRow(dataType, columnType));
            int? max = r.IsDBNull(6) ? null : (int)Math.Min(Convert.ToInt64(r.GetValue(6)), int.MaxValue);
            table.Columns.Add(new DbColumn(
                Name: r.GetString(3),
                SqlType: columnType ?? (max is > 0 ? $"{dataType}({max})" : max == -1 ? $"{dataType}(max)" : dataType),
                ClrType: clr ?? "",
                Nullable: r.GetString(5) == "YES",
                IsPrimaryKey: Convert.ToInt32(r.GetValue(9)) == 1,
                IsIdentity: Convert.ToInt32(r.GetValue(7)) == 1,
                IsComputed: Convert.ToInt32(r.GetValue(8)) == 1 || dataType is "timestamp" && columnType is null || dataType == "rowversion",
                MaxLength: max,
                Supported: clr is not null));
        }
        return tables;
    }

    private static string? SqlServerType(string t) => t switch
    {
        "int" => "int",
        "bigint" => "long",
        "smallint" => "short",
        "tinyint" => "byte",
        "bit" => "bool",
        "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
        "float" => "double",
        "real" => "float",
        "date" => "DateOnly",
        "datetime" or "datetime2" or "smalldatetime" => "DateTime",
        "datetimeoffset" => "DateTimeOffset",
        "time" => "TimeOnly",
        "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" or "xml" => "string",
        "uniqueidentifier" => "Guid",
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "byte[]",
        _ => null, // geography, geometry, hierarchyid, sql_variant…
    };

    private static string? MySqlType(string t, string columnType)
    {
        var unsigned = columnType.Contains("unsigned");
        return t switch
        {
            "tinyint" when columnType.StartsWith("tinyint(1)") => "bool",
            "bit" when columnType is "bit(1)" or "bit" => "bool",
            "bit" => "ulong",
            "tinyint" => unsigned ? "byte" : "sbyte",
            "smallint" => unsigned ? "ushort" : "short",
            "mediumint" or "int" or "integer" => unsigned ? "uint" : "int",
            "bigint" => unsigned ? "ulong" : "long",
            "decimal" or "numeric" => "decimal",
            "float" => "float",
            "double" or "real" => "double",
            "date" => "DateOnly",
            "datetime" or "timestamp" => "DateTime",
            "time" => "TimeSpan",
            "year" => "short",
            "char" or "varchar" or "text" or "tinytext" or "mediumtext" or "longtext" or "enum" or "set" or "json" => "string",
            "binary" or "varbinary" or "blob" or "tinyblob" or "mediumblob" or "longblob" => "byte[]",
            _ => null,
        };
    }
}
