using System.Data.Common;
using Stackwright.Server.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using DbColumn = Stackwright.Server.Models.DbColumn;

namespace Stackwright.Server.Services;

/// <summary>Reads database structure (databases/schemas, tables, columns) from SQL Server, PostgreSQL, MySQL, Oracle and SQLite.</summary>
public class DatabaseService
{
    private static readonly HashSet<string> MySqlSystemDbs = new(StringComparer.OrdinalIgnoreCase)
        { "information_schema", "mysql", "performance_schema", "sys" };

    private static readonly HashSet<string> OracleSystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYS", "SYSTEM", "OUTLN", "DBSNMP", "APPQOSSYS", "AUDSYS", "CTXSYS", "DVSYS", "DVF", "GSMADMIN_INTERNAL", "LBACSYS", "MDSYS",
        "OJVMSYS", "OLAPSYS", "ORDDATA", "ORDSYS", "ORDPLUGINS", "SI_INFORMTN_SCHEMA", "WMSYS", "XDB", "ANONYMOUS", "XS$NULL",
        "DIP", "GGSYS", "GSMCATUSER", "GSMUSER", "REMOTE_SCHEDULER_AGENT", "SYSBACKUP", "SYSDG", "SYSKM", "SYSRAC", "SYS$UMF",
        "MDDATA", "ORACLE_OCM", "SPATIAL_CSW_ADMIN_USR", "SPATIAL_WFS_ADMIN_USR", "FLOWS_FILES", "APEX_PUBLIC_USER", "PDBADMIN",
        "GSMROOTUSER", "DGPDB_INT", "BAASSYS", "OPS$ORACLE", "VECSYS",
    };

    public static int DefaultPort(DbProvider p) => p switch
    {
        DbProvider.SqlServer => 1433,
        DbProvider.MySql => 3306,
        DbProvider.PostgreSql => 5432,
        DbProvider.Oracle => 1521,
        _ => 0,
    };

    // ------------------------------------------------------------------ Connection strings

    public string ConnectionString(DbConnectionInfo c, bool forApp = false)
    {
        if (string.IsNullOrWhiteSpace(c.Server))
            throw new StudioException(c.Provider == DbProvider.Sqlite ? "Please choose the database file." : "Please enter the server name.");
        var host = c.Server.Trim();
        var port = c.Port is > 0 ? c.Port.Value : DefaultPort(c.Provider);
        switch (c.Provider)
        {
            case DbProvider.SqlServer:
            {
                var b = new SqlConnectionStringBuilder
                {
                    DataSource = c.Port is > 0 ? $"{host},{c.Port}" : host,
                    IntegratedSecurity = c.IntegratedSecurity,
                    TrustServerCertificate = true,
                    MultipleActiveResultSets = forApp,
                };
                if (!forApp) b.ConnectTimeout = 8;
                if (!c.IntegratedSecurity) { b.UserID = c.Username ?? ""; b.Password = c.Password ?? ""; }
                if (!string.IsNullOrWhiteSpace(c.Database)) b.InitialCatalog = c.Database;
                return b.ConnectionString;
            }
            case DbProvider.MySql:
            {
                var b = new MySqlConnectionStringBuilder { Server = host, Port = (uint)port, UserID = c.Username ?? "root", Password = c.Password ?? "" };
                if (!forApp) b.ConnectionTimeout = 8;
                if (!string.IsNullOrWhiteSpace(c.Database)) b.Database = c.Database;
                return b.ConnectionString;
            }
            case DbProvider.PostgreSql:
            {
                var b = new NpgsqlConnectionStringBuilder
                {
                    Host = host, Port = port, Username = c.Username ?? "postgres", Password = c.Password ?? "",
                    Database = string.IsNullOrWhiteSpace(c.Database) ? "postgres" : c.Database,
                };
                if (!forApp) b.Timeout = 8;
                return b.ConnectionString;
            }
            case DbProvider.Oracle:
            {
                if (string.IsNullOrWhiteSpace(c.ServiceName)) throw new StudioException("Please enter the Oracle service name (for example ORCLPDB1, XEPDB1 or FREEPDB1).");
                var b = new OracleConnectionStringBuilder
                {
                    DataSource = $"{host}:{port}/{c.ServiceName.Trim()}",
                    UserID = c.Username ?? "",
                    Password = c.Password ?? "",
                };
                if (!forApp) b.ConnectionTimeout = 10;
                return b.ConnectionString;
            }
            case DbProvider.Sqlite:
            {
                var path = Path.GetFullPath(host);
                return new SqliteConnectionStringBuilder { DataSource = path, Mode = forApp ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly }.ConnectionString;
            }
            default:
                throw new StudioException("This database type isn't supported.");
        }
    }

    private async Task<DbConnection> OpenAsync(DbConnectionInfo c, CancellationToken ct)
    {
        if (c.Provider == DbProvider.Sqlite && !File.Exists(c.Server?.Trim()))
            throw new StudioException("That SQLite database file doesn't exist.");
        var cs = ConnectionString(c);
        DbConnection conn = c.Provider switch
        {
            DbProvider.SqlServer => new SqlConnection(cs),
            DbProvider.MySql => new MySqlConnection(cs),
            DbProvider.PostgreSql => new NpgsqlConnection(cs),
            DbProvider.Oracle => new OracleConnection(cs),
            _ => new SqliteConnection(cs),
        };
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            await conn.DisposeAsync();
            throw new StudioException("Couldn't connect to the database. " + FriendlyError(ex));
        }
    }

    private static string FriendlyError(Exception ex)
    {
        var m = ex.Message;
        if (m.Contains("Login failed", StringComparison.OrdinalIgnoreCase) || m.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            || m.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) || m.Contains("ORA-01017"))
            return "The username or password is wrong, or this login isn't allowed.";
        if (m.Contains("ORA-12514") || m.Contains("ORA-12505"))
            return "The server is reachable but doesn't know that service name. Check the service name (for example ORCLPDB1).";
        if (m.Contains("ORA-12541") || m.Contains("ORA-12170") || m.Contains("network-related", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) || m.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase)
            || m.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
            return "The server couldn't be reached. Check the server name, the port and that the database is running.";
        if (m.Contains("does not exist", StringComparison.OrdinalIgnoreCase) && m.Contains("database", StringComparison.OrdinalIgnoreCase))
            return "That database doesn't exist on the server.";
        return m;
    }

    // ------------------------------------------------------------------ Databases / schemas

    /// <summary>Connects and lists what the user can pick next: databases (SQL Server, MySQL, PostgreSQL) or schemas (Oracle).</summary>
    public async Task<(string Version, List<string> Databases)> TestAsync(DbConnectionInfo c, CancellationToken ct)
    {
        await using var conn = await OpenAsync(c.Provider is DbProvider.Oracle or DbProvider.Sqlite ? c : c with { Database = null }, ct);
        var version = conn.ServerVersion;
        var list = new List<string>();
        switch (c.Provider)
        {
            case DbProvider.Sqlite:
                list.Add(Path.GetFileNameWithoutExtension(c.Server));
                return ($"SQLite {version}", list);
            case DbProvider.SqlServer:
                await ReadStrings(conn, "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name", list, ct);
                break;
            case DbProvider.MySql:
                await ReadStrings(conn, "SHOW DATABASES", list, ct);
                list.RemoveAll(MySqlSystemDbs.Contains);
                break;
            case DbProvider.PostgreSql:
                await ReadStrings(conn, "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn ORDER BY datname", list, ct);
                break;
            case DbProvider.Oracle:
                try { await ReadStrings(conn, "SELECT USERNAME FROM ALL_USERS WHERE ORACLE_MAINTAINED = 'N' ORDER BY USERNAME", list, ct); }
                catch (DbException) { await ReadStrings(conn, "SELECT USERNAME FROM ALL_USERS ORDER BY USERNAME", list, ct); }
                list.RemoveAll(s => OracleSystemSchemas.Contains(s) || s.StartsWith("APEX_", StringComparison.OrdinalIgnoreCase));
                // The signed-in user's own schema first: that's where their tables usually are
                var me = (c.Username ?? "").Trim().ToUpperInvariant();
                if (me.Length > 0) { list.RemoveAll(s => s.Equals(me, StringComparison.OrdinalIgnoreCase)); list.Insert(0, me); }
                version = $"Oracle {version}";
                break;
        }
        return (version, list);
    }

    private static async Task ReadStrings(DbConnection conn, string sql, List<string> into, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) into.Add(r.GetString(0));
    }

    public async Task<string> ServerVersionAsync(DbConnectionInfo c, CancellationToken ct)
    {
        await using var conn = await OpenAsync(c, ct);
        return conn.ServerVersion;
    }

    // ------------------------------------------------------------------ Tables & columns

    public async Task<List<DbTable>> GetTablesAsync(DbConnectionInfo c, CancellationToken ct)
    {
        if (c.Provider != DbProvider.Sqlite && string.IsNullOrWhiteSpace(c.Database))
            throw new StudioException(c.Provider == DbProvider.Oracle ? "Please choose a schema." : "Please choose a database.");
        await using var conn = await OpenAsync(c, ct);
        return c.Provider switch
        {
            DbProvider.SqlServer => await SqlServerTables(conn, ct),
            DbProvider.MySql => await MySqlTables(conn, c.Database!, ct),
            DbProvider.PostgreSql => await PostgresTables(conn, ct),
            DbProvider.Oracle => await OracleTables(conn, c.Database!.Trim(), ct),
            _ => await SqliteTables(conn, ct),
        };
    }

    /// <summary>One column as read from the catalog, before it is turned into a <see cref="DbColumn"/>.</summary>
    private sealed record RawColumn(string Schema, string Table, bool IsView, string Column, string DataType, bool Nullable,
        int? MaxLength, bool Identity, bool Computed, bool PrimaryKey, string? ClrType, string SqlType, long? Rows);

    private static List<DbTable> Assemble(IEnumerable<RawColumn> rows)
    {
        var tables = new List<DbTable>();
        var index = new Dictionary<(string, string), DbTable>();
        foreach (var r in rows)
        {
            if (!index.TryGetValue((r.Schema, r.Table), out var t))
            {
                t = new DbTable(r.Schema, r.Table, [], r.IsView, r.Rows);
                index[(r.Schema, r.Table)] = t;
                tables.Add(t);
            }
            t.Columns.Add(new DbColumn(r.Column, r.SqlType, r.ClrType ?? "", r.Nullable, r.PrimaryKey, r.Identity, r.Computed, r.MaxLength, r.ClrType is not null));
        }
        return tables;
    }

    private static async Task<List<RawColumn>> Query(DbConnection conn, string sql, Func<DbDataReader, RawColumn> map, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (cmd is OracleCommand oc) oc.BindByName = true;
        foreach (var (n, v) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v;
            cmd.Parameters.Add(p);
        }
        var list = new List<RawColumn>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    private static int? IntOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : (int)Math.Min(Convert.ToInt64(r.GetValue(i)), int.MaxValue);
    private static long? LongOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static bool Flag(DbDataReader r, int i) => !r.IsDBNull(i) && Convert.ToInt32(r.GetValue(i)) == 1;

    // ---- SQL Server

    private static async Task<List<DbTable>> SqlServerTables(DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH,
                   CAST(ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity'), 0) AS int),
                   CAST(ISNULL(COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsComputed'), 0) AS int),
                   CASE WHEN pk.COLUMN_NAME IS NULL THEN 0 ELSE 1 END,
                   (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(QUOTENAME(t.TABLE_SCHEMA) + '.' + QUOTENAME(t.TABLE_NAME)) AND p.index_id IN (0, 1))
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
        var rows = await Query(conn, sql, r =>
        {
            var type = r.GetString(4).ToLowerInvariant();
            var max = IntOrNull(r, 6);
            return new RawColumn(r.GetString(0), r.GetString(1), r.GetString(2).Contains("VIEW"), r.GetString(3), type, r.GetString(5) == "YES",
                max, Flag(r, 7), Flag(r, 8) || type is "timestamp" or "rowversion", Flag(r, 9), SqlServerType(type),
                max is > 0 ? $"{type}({max})" : max == -1 ? $"{type}(max)" : type, LongOrNull(r, 10));
        }, ct);
        return Assemble(rows);
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

    // ---- MySQL

    private static async Task<List<DbTable>> MySqlTables(DbConnection conn, string db, CancellationToken ct)
    {
        const string sql = """
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH,
                   CASE WHEN c.EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END,
                   CASE WHEN c.EXTRA LIKE '%GENERATED%' THEN 1 ELSE 0 END,
                   CASE WHEN c.COLUMN_KEY = 'PRI' THEN 1 ELSE 0 END,
                   c.COLUMN_TYPE, t.TABLE_ROWS
            FROM information_schema.TABLES t
            JOIN information_schema.COLUMNS c ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME
            WHERE t.TABLE_SCHEMA = @db AND t.TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY t.TABLE_NAME, c.ORDINAL_POSITION
            """;
        var rows = await Query(conn, sql, r =>
        {
            var type = r.GetString(4).ToLowerInvariant();
            var colType = r.GetString(10).ToLowerInvariant();
            return new RawColumn(r.GetString(0), r.GetString(1), r.GetString(2).Contains("VIEW"), r.GetString(3), type, r.GetString(5) == "YES",
                IntOrNull(r, 6), Flag(r, 7), Flag(r, 8), Flag(r, 9), MySqlType(type, colType), colType, LongOrNull(r, 11));
        }, ct, ("@db", db));
        return Assemble(rows);
    }

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

    // ---- PostgreSQL

    private static async Task<List<DbTable>> PostgresTables(DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT c.table_schema, c.table_name, t.table_type, c.column_name, c.data_type, c.is_nullable, c.character_maximum_length,
                   CASE WHEN c.is_identity = 'YES' OR c.column_default LIKE 'nextval(%' THEN 1 ELSE 0 END,
                   CASE WHEN c.is_generated = 'ALWAYS' THEN 1 ELSE 0 END,
                   CASE WHEN pk.column_name IS NULL THEN 0 ELSE 1 END,
                   c.udt_name, s.n_live_tup
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            LEFT JOIN (
                SELECT kcu.table_schema, kcu.table_name, kcu.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
            ) pk ON pk.table_schema = c.table_schema AND pk.table_name = c.table_name AND pk.column_name = c.column_name
            LEFT JOIN pg_stat_user_tables s ON s.schemaname = c.table_schema AND s.relname = c.table_name
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema') AND c.table_schema NOT LIKE 'pg_toast%'
              AND c.table_name <> '__EFMigrationsHistory'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;
        var rows = await Query(conn, sql, r =>
        {
            var type = r.GetString(4).ToLowerInvariant();
            var max = IntOrNull(r, 6);
            var udt = r.GetString(10);
            return new RawColumn(r.GetString(0), r.GetString(1), r.GetString(2).Contains("VIEW"), r.GetString(3), type, r.GetString(5) == "YES",
                max, Flag(r, 7), Flag(r, 8), Flag(r, 9), PostgresType(type), max is > 0 ? $"{udt}({max})" : udt, LongOrNull(r, 11));
        }, ct);
        return Assemble(rows);
    }

    private static string? PostgresType(string t) => t switch
    {
        "integer" => "int",
        "bigint" => "long",
        "smallint" => "short",
        "boolean" => "bool",
        "numeric" or "money" => "decimal",
        "real" => "float",
        "double precision" => "double",
        "text" or "character varying" or "character" or "citext" or "json" or "jsonb" or "xml" or "name" or "inet" or "cidr" => "string",
        "uuid" => "Guid",
        "date" => "DateOnly",
        "timestamp without time zone" or "timestamp with time zone" => "DateTime",
        "time without time zone" => "TimeOnly",
        "interval" => "TimeSpan",
        "bytea" => "byte[]",
        _ => null, // arrays, enums and other custom types
    };

    // ---- Oracle

    private static async Task<List<DbTable>> OracleTables(DbConnection conn, string owner, CancellationToken ct)
    {
        const string modern = """
            SELECT c.OWNER, c.TABLE_NAME, CASE WHEN v.VIEW_NAME IS NULL THEN 'TABLE' ELSE 'VIEW' END, c.COLUMN_NAME, c.DATA_TYPE, c.NULLABLE,
                   c.CHAR_LENGTH, CASE WHEN c.IDENTITY_COLUMN = 'YES' THEN 1 ELSE 0 END, CASE WHEN c.VIRTUAL_COLUMN = 'YES' THEN 1 ELSE 0 END,
                   CASE WHEN pk.COLUMN_NAME IS NULL THEN 0 ELSE 1 END, c.DATA_PRECISION, c.DATA_SCALE, t.NUM_ROWS
            FROM ALL_TAB_COLS c
            LEFT JOIN ALL_VIEWS v ON v.OWNER = c.OWNER AND v.VIEW_NAME = c.TABLE_NAME
            LEFT JOIN ALL_TABLES t ON t.OWNER = c.OWNER AND t.TABLE_NAME = c.TABLE_NAME
            LEFT JOIN (
                SELECT cc.OWNER, cc.TABLE_NAME, cc.COLUMN_NAME FROM ALL_CONSTRAINTS k
                JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = k.OWNER AND cc.CONSTRAINT_NAME = k.CONSTRAINT_NAME
                WHERE k.CONSTRAINT_TYPE = 'P'
            ) pk ON pk.OWNER = c.OWNER AND pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE c.OWNER = :owner AND c.HIDDEN_COLUMN = 'NO' AND (t.TABLE_NAME IS NOT NULL OR v.VIEW_NAME IS NOT NULL)
              AND c.TABLE_NAME NOT LIKE 'BIN$%' AND c.TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY c.TABLE_NAME, c.COLUMN_ID
            """;
        // Oracle 11g has no identity/virtual flags on ALL_TAB_COLS
        var legacy = modern.Replace("CASE WHEN c.IDENTITY_COLUMN = 'YES' THEN 1 ELSE 0 END", "0").Replace("CASE WHEN c.VIRTUAL_COLUMN = 'YES' THEN 1 ELSE 0 END", "0");

        RawColumn Map(DbDataReader r)
        {
            var type = r.GetString(4).ToUpperInvariant();
            var precision = IntOrNull(r, 10);
            var scale = IntOrNull(r, 11);
            var max = IntOrNull(r, 6);
            var sqlType = type == "NUMBER" ? precision is null ? "NUMBER" : $"NUMBER({precision},{scale ?? 0})"
                : max is > 0 && type.Contains("CHAR") ? $"{type}({max})" : type;
            return new RawColumn(r.GetString(0), r.GetString(1), r.GetString(2) == "VIEW", r.GetString(3), type, r.GetString(5) == "Y",
                type.Contains("CHAR") ? max : null, Flag(r, 7), Flag(r, 8), Flag(r, 9), OracleType(type, precision, scale), sqlType, LongOrNull(r, 12));
        }

        List<RawColumn> rows;
        try { rows = await Query(conn, modern, Map, ct, (":owner", owner.ToUpperInvariant())); }
        catch (OracleException) { rows = await Query(conn, legacy, Map, ct, (":owner", owner.ToUpperInvariant())); }
        if (rows.Count == 0 && owner != owner.ToUpperInvariant())
            rows = await Query(conn, modern, Map, ct, (":owner", owner));
        return Assemble(rows);
    }

    private static string? OracleType(string t, int? precision, int? scale)
    {
        if (t == "NUMBER")
        {
            if (precision is null) return scale == 0 ? "long" : "decimal"; // NUMBER without precision can hold anything
            if ((scale ?? 0) > 0) return "decimal";
            return precision <= 9 ? "int" : precision <= 18 ? "long" : "decimal";
        }
        if (t.StartsWith("TIMESTAMP") && t.Contains("TIME ZONE")) return "DateTimeOffset";
        if (t.StartsWith("TIMESTAMP")) return "DateTime";
        if (t.StartsWith("INTERVAL DAY")) return "TimeSpan";
        return t switch
        {
            "INTEGER" => "int",
            "FLOAT" or "BINARY_DOUBLE" => "double",
            "BINARY_FLOAT" => "float",
            "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "CLOB" or "NCLOB" or "LONG" or "ROWID" or "UROWID" => "string",
            "DATE" => "DateTime", // Oracle DATE also stores the time
            "RAW" or "BLOB" or "LONG RAW" => "byte[]",
            _ => null, // XMLTYPE, SDO_GEOMETRY and other object types
        };
    }

    // ---- SQLite

    private static async Task<List<DbTable>> SqliteTables(DbConnection conn, CancellationToken ct)
    {
        var objects = new List<(string Name, bool IsView)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) objects.Add((r.GetString(0), r.GetString(1) == "view"));
        }

        var rows = new List<RawColumn>();
        foreach (var (name, isView) in objects)
        {
            long? count = null;
            if (!isView)
            {
                await using var cc = conn.CreateCommand();
                cc.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"")}\"";
                try { count = Convert.ToInt64(await cc.ExecuteScalarAsync(ct)); } catch { /* ignore */ }
            }
            var cols = new List<(string Name, string Type, bool NotNull, int Pk)>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info(\"{name.Replace("\"", "\"\"")}\")";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) cols.Add((r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt64(3) == 1, (int)r.GetInt64(5)));
            }
            var pkCount = cols.Count(c => c.Pk > 0);
            foreach (var c in cols)
            {
                var upper = c.Type.ToUpperInvariant();
                var identity = c.Pk > 0 && pkCount == 1 && upper == "INTEGER";
                var max = System.Text.RegularExpressions.Regex.Match(upper, @"\((\d+)\)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : (int?)null;
                rows.Add(new RawColumn("", name, isView, c.Name, upper, !c.NotNull && c.Pk == 0, max, identity, false, c.Pk > 0,
                    SqliteType(upper), c.Type.Length == 0 ? "(any)" : c.Type, count));
            }
        }
        return Assemble(rows);
    }

    /// <summary>SQLite uses type affinity: the declared type name only hints at the storage class.</summary>
    private static string SqliteType(string t)
    {
        if (t.Contains("BOOL")) return "bool";
        if (t.Contains("INT")) return "long";
        if (t.Contains("CHAR") || t.Contains("CLOB") || t.Contains("TEXT")) return "string";
        if (t.Contains("BLOB")) return "byte[]";
        if (t.Contains("REAL") || t.Contains("FLOA") || t.Contains("DOUB")) return "double";
        if (t.Contains("DEC") || t.Contains("NUMERIC") || t.Contains("MONEY")) return "decimal";
        if (t.Contains("DATE") || t.Contains("TIME")) return "DateTime";
        if (t is "GUID" or "UUID" or "UNIQUEIDENTIFIER") return "Guid";
        return "string";
    }
}
