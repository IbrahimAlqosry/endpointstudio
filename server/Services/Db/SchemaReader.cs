using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using Stackwright.Server.Models;

namespace Stackwright.Server.Services.Db;

/// <summary>Reads a database's full design (tables, columns, keys, relations, indexes, views, routines) from its catalog.</summary>
public static class SchemaReader
{
    public static async Task<DbSchema> ReadAsync(DbConnection conn, DbConnectionInfo info, CancellationToken ct)
    {
        var db = info.Database ?? "";
        var s = info.Provider switch
        {
            DbProvider.SqlServer => await SqlServer(conn, ct),
            DbProvider.PostgreSql => await Postgres(conn, ct),
            DbProvider.MySql => await MySql(conn, db, ct),
            DbProvider.Oracle => await Oracle(conn, db.ToUpperInvariant(), ct),
            _ => await Sqlite(conn, ct),
        };
        // PostgreSQL, MySQL and Oracle only keep row estimates, which lag behind recent changes. Small tables are cheap to count exactly.
        var dialect = SqlDialect.For(info.Provider);
        var unknown = info.Provider is DbProvider.SqlServer or DbProvider.Sqlite ? []
            : s.Tables.Select((t, i) => (t, i)).Where(x => x.t.Rows is null or < 50_000).Take(300).ToList();
        foreach (var (t, i) in unknown)
        {
            try { await Each(conn, $"SELECT COUNT(*) FROM {dialect.Table(t.Schema, t.Name)}", r => s.Tables[i] = t with { Rows = Long(r, 0) }, ct); }
            catch (DbException) { /* no permission: keep the estimate */ }
        }
        var types = dialect.CommonTypes;
        var schemas = s.Tables.Select(t => t.Schema).Concat(s.Views.Select(v => v.Schema)).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList();
        var caps = new DbsCapabilities(
            Schemas: info.Provider is DbProvider.SqlServer or DbProvider.PostgreSql,
            AlterColumn: info.Provider != DbProvider.Sqlite,
            AddForeignKey: info.Provider != DbProvider.Sqlite,
            Routines: info.Provider != DbProvider.Sqlite,
            RenameColumn: true);
        return new DbSchema(info.Provider.ToString(), info.Provider == DbProvider.Sqlite ? Path.GetFileName(info.Server) : db, conn.ServerVersion,
            s.Tables, s.Fks, s.Indexes, s.Views, s.Routines, types, schemas, caps);
    }

    private sealed class Raw
    {
        public List<DbsTable> Tables { get; } = [];
        public List<DbsForeignKey> Fks { get; } = [];
        public List<DbsIndex> Indexes { get; } = [];
        public List<DbsView> Views { get; } = [];
        public List<DbsRoutine> Routines { get; } = [];
        private readonly Dictionary<(string, string), DbsTable> _byName = new();

        public DbsTable Table(string schema, string name, long? rows = null)
        {
            if (_byName.TryGetValue((schema, name), out var t)) return t;
            t = new DbsTable(schema, name, rows, [], [], null);
            _byName[(schema, name)] = t;
            Tables.Add(t);
            return t;
        }
        public DbsTable? Find(string schema, string name) => _byName.GetValueOrDefault((schema, name));
        public void SetPk(string schema, string name, string pkName)
        {
            if (_byName.TryGetValue((schema, name), out var t))
            {
                var updated = t with { PkName = pkName };
                _byName[(schema, name)] = updated;
                Tables[Tables.IndexOf(t)] = updated;
            }
        }
    }

    private static async Task Each(DbConnection conn, string sql, Action<DbDataReader> row, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        if (cmd is OracleCommand oc) { oc.BindByName = true; oc.InitialLONGFetchSize = -1; }
        foreach (var (n, v) in ps) { var p = cmd.CreateParameter(); p.ParameterName = n.TrimStart(':'); p.Value = v; cmd.Parameters.Add(p); }
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) row(r);
    }

    private static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private static int? Int(DbDataReader r, int i) => r.IsDBNull(i) ? null : (int)Math.Min(Convert.ToInt64(r.GetValue(i)), int.MaxValue);
    private static long? Long(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
    private static bool Flag(DbDataReader r, int i) => !r.IsDBNull(i) && Convert.ToInt64(r.GetValue(i)) != 0;

    private static void AddFk(Raw raw, string name, string s, string t, string c, string rs, string rt, string rc, string del, string upd)
    {
        var existing = raw.Fks.FindIndex(f => f.Name == name && f.Schema == s && f.Table == t);
        if (existing >= 0)
        {
            raw.Fks[existing].Columns.Add(c);
            raw.Fks[existing].RefColumns.Add(rc);
        }
        else raw.Fks.Add(new DbsForeignKey(name, s, t, [c], rs, rt, [rc], Action(del), Action(upd)));
    }

    private static void AddIndex(Raw raw, string name, string s, string t, bool unique, string column)
    {
        var existing = raw.Indexes.FindIndex(i => i.Name == name && i.Schema == s && i.Table == t);
        if (existing >= 0) raw.Indexes[existing].Columns.Add(column);
        else raw.Indexes.Add(new DbsIndex(name, s, t, unique, [column]));
    }

    private static string Action(string? a) => (a ?? "").ToUpperInvariant().Replace('_', ' ') switch
    {
        "C" or "CASCADE" => "CASCADE",
        "N" or "SET NULL" => "SET NULL",
        "D" or "SET DEFAULT" => "SET DEFAULT",
        "R" or "RESTRICT" => "RESTRICT",
        _ => "NO ACTION",
    };

    // ------------------------------------------------------------------ SQL Server

    private static async Task<Raw> SqlServer(DbConnection conn, CancellationToken ct)
    {
        var raw = new Raw();
        await Each(conn, """
            SELECT s.name, t.name, (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1)),
                   c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed,
                   OBJECT_DEFINITION(c.default_object_id), c.column_id
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0 AND t.name NOT IN ('sysdiagrams', '__EFMigrationsHistory')
            ORDER BY s.name, t.name, c.column_id
            """, r =>
        {
            var type = r.GetString(4);
            var len = Convert.ToInt32(r.GetValue(5));
            var full = type switch
            {
                "nvarchar" or "nchar" => $"{type}({(len == -1 ? "max" : (len / 2).ToString())})",
                "varchar" or "char" or "varbinary" or "binary" => $"{type}({(len == -1 ? "max" : len.ToString())})",
                "decimal" or "numeric" => $"{type}({r.GetValue(6)},{r.GetValue(7)})",
                "datetime2" or "time" or "datetimeoffset" when Convert.ToInt32(r.GetValue(7)) != 7 => $"{type}({r.GetValue(7)})",
                _ => type,
            };
            var t = raw.Table(r.GetString(0), r.GetString(1), Long(r, 2));
            t.Columns.Add(new DbsColumn(r.GetString(3), full, Flag(r, 8), CleanDefault(Str(r, 11)), Flag(r, 9), false, Flag(r, 10), Convert.ToInt32(r.GetValue(12)),
                type.Contains("char") ? (len == -1 ? null : type.StartsWith('n') ? len / 2 : len) : null));
        }, ct);
        await Each(conn, """
            SELECT s.name, t.name, k.name, c.name FROM sys.key_constraints k
            JOIN sys.tables t ON t.object_id = k.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE k.type = 'PK' ORDER BY s.name, t.name, ic.key_ordinal
            """, r => MarkPk(raw, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);
        await Each(conn, """
            SELECT fk.name, SCHEMA_NAME(tp.schema_id), tp.name, cp.name, SCHEMA_NAME(tr.schema_id), tr.name, cr.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc
            FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables tp ON tp.object_id = fkc.parent_object_id JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.tables tr ON tr.object_id = fkc.referenced_object_id JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            ORDER BY fk.name, fkc.constraint_column_id
            """, r => AddFk(raw, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)), ct);
        await Each(conn, """
            SELECT s.name, t.name, i.name, i.is_unique, c.name FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 0 AND i.type > 0 AND t.is_ms_shipped = 0 AND i.is_unique_constraint = 0
            ORDER BY s.name, t.name, i.name, ic.key_ordinal
            """, r => AddIndex(raw, r.GetString(2), r.GetString(0), r.GetString(1), Flag(r, 3), r.GetString(4)), ct);
        await Each(conn, "SELECT s.name, v.name, OBJECT_DEFINITION(v.object_id) FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id WHERE v.is_ms_shipped = 0 ORDER BY s.name, v.name",
            r => raw.Views.Add(new DbsView(r.GetString(0), r.GetString(1), Str(r, 2))), ct);
        await Each(conn, """
            SELECT s.name, o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0 AND o.name NOT LIKE 'sp[_]%diagram%' AND o.name NOT LIKE 'fn[_]diagramobjects'
            ORDER BY s.name, o.name
            """, r => raw.Routines.Add(new DbsRoutine(r.GetString(0), r.GetString(1), r.GetString(2).Trim() == "P" ? "Procedure" : "Function")), ct);
        return raw;
    }

    private static void MarkPk(Raw raw, string schema, string table, string pkName, string column)
    {
        var t = raw.Find(schema, table);
        if (t is null) return;
        t.PrimaryKey.Add(column);
        var i = t.Columns.FindIndex(c => c.Name == column);
        if (i >= 0) t.Columns[i] = t.Columns[i] with { PrimaryKey = true };
        raw.SetPk(schema, table, pkName);
    }

    private static string? CleanDefault(string? d)
    {
        if (d is null) return null;
        d = d.Trim();
        while (d.Length > 1 && d[0] == '(' && d[^1] == ')') d = d[1..^1];
        return d;
    }

    // ------------------------------------------------------------------ PostgreSQL

    private static async Task<Raw> Postgres(DbConnection conn, CancellationToken ct)
    {
        var raw = new Raw();
        await Each(conn, """
            SELECT c.table_schema, c.table_name, s.n_live_tup, c.column_name,
                   CASE WHEN c.data_type IN ('character varying','character') AND c.character_maximum_length IS NOT NULL THEN (CASE c.data_type WHEN 'character varying' THEN 'varchar' ELSE 'char' END) || '(' || c.character_maximum_length || ')'
                        WHEN c.data_type = 'numeric' AND c.numeric_precision IS NOT NULL THEN 'numeric(' || c.numeric_precision || ',' || c.numeric_scale || ')'
                        WHEN c.data_type = 'USER-DEFINED' OR c.data_type = 'ARRAY' THEN c.udt_name
                        ELSE c.data_type END,
                   c.is_nullable = 'YES', c.column_default, c.is_identity = 'YES' OR COALESCE(c.column_default, '') LIKE 'nextval(%', c.is_generated = 'ALWAYS', c.ordinal_position, c.character_maximum_length
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            LEFT JOIN pg_stat_user_tables s ON s.schemaname = c.table_schema AND s.relname = c.table_name
            WHERE c.table_schema NOT IN ('pg_catalog','information_schema') AND c.table_schema NOT LIKE 'pg_toast%'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """, r =>
        {
            var t = raw.Table(r.GetString(0), r.GetString(1), Long(r, 2));
            var identity = Convert.ToBoolean(r.GetValue(7));
            var def = Str(r, 6);
            t.Columns.Add(new DbsColumn(r.GetString(3), r.GetString(4), Convert.ToBoolean(r.GetValue(5)), identity && def?.StartsWith("nextval(") == true ? null : def,
                identity, false, Convert.ToBoolean(r.GetValue(8)), Convert.ToInt32(r.GetValue(9)), Int(r, 10)));
        }, ct);
        await Each(conn, """
            SELECT tc.table_schema, tc.table_name, tc.constraint_name, kcu.column_name FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
            WHERE tc.constraint_type = 'PRIMARY KEY' ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position
            """, r => MarkPk(raw, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);
        await Each(conn, """
            SELECT con.conname, ns.nspname, cl.relname, att.attname, fns.nspname, fcl.relname, fatt.attname, con.confdeltype::text, con.confupdtype::text
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid JOIN pg_namespace ns ON ns.oid = cl.relnamespace
            JOIN pg_class fcl ON fcl.oid = con.confrelid JOIN pg_namespace fns ON fns.oid = fcl.relnamespace
            CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS u(attnum, fattnum, ord)
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = u.attnum
            JOIN pg_attribute fatt ON fatt.attrelid = con.confrelid AND fatt.attnum = u.fattnum
            WHERE con.contype = 'f' ORDER BY con.conname, u.ord
            """, r => AddFk(raw, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)), ct);
        await Each(conn, """
            SELECT n.nspname, t.relname, i.relname, ix.indisunique, a.attname
            FROM pg_index ix JOIN pg_class t ON t.oid = ix.indrelid JOIN pg_class i ON i.oid = ix.indexrelid JOIN pg_namespace n ON n.oid = t.relnamespace
            CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE NOT ix.indisprimary AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%'
              AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = ix.indexrelid AND c.contype = 'u')
            ORDER BY n.nspname, t.relname, i.relname, k.ord
            """, r => AddIndex(raw, r.GetString(2), r.GetString(0), r.GetString(1), Convert.ToBoolean(r.GetValue(3)), r.GetString(4)), ct);
        await Each(conn, "SELECT table_schema, table_name, view_definition FROM information_schema.views WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2",
            r => raw.Views.Add(new DbsView(r.GetString(0), r.GetString(1), Str(r, 2))), ct);
        await Each(conn, """
            SELECT n.nspname, p.proname, CASE p.prokind WHEN 'p' THEN 'Procedure' ELSE 'Function' END
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            LEFT JOIN pg_depend d ON d.objid = p.oid AND d.deptype = 'e'
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND p.prokind IN ('f','p') AND d.objid IS NULL
            ORDER BY 1, 2
            """, r => raw.Routines.Add(new DbsRoutine(r.GetString(0), r.GetString(1), r.GetString(2))), ct);
        return raw;
    }

    // ------------------------------------------------------------------ MySQL

    private static async Task<Raw> MySql(DbConnection conn, string db, CancellationToken ct)
    {
        var raw = new Raw();
        await Each(conn, """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, t.TABLE_ROWS, c.COLUMN_NAME, c.COLUMN_TYPE, c.IS_NULLABLE = 'YES', c.COLUMN_DEFAULT,
                   c.EXTRA LIKE '%auto_increment%', c.EXTRA LIKE '%GENERATED%', c.ORDINAL_POSITION, c.COLUMN_KEY = 'PRI', c.CHARACTER_MAXIMUM_LENGTH
            FROM information_schema.COLUMNS c JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_TYPE = 'BASE TABLE'
            WHERE c.TABLE_SCHEMA = @db ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
            """, r =>
        {
            var t = raw.Table("", r.GetString(1), Long(r, 2));
            var pk = Flag(r, 10);
            t.Columns.Add(new DbsColumn(r.GetString(3), r.GetString(4), Flag(r, 5), Str(r, 6), Flag(r, 7), pk, Flag(r, 8), Convert.ToInt32(r.GetValue(9)), Int(r, 11)));
            if (pk) t.PrimaryKey.Add(r.GetString(3));
        }, ct, ("@db", db));
        foreach (var t in raw.Tables.ToList().Where(t => t.PrimaryKey.Count > 0)) raw.SetPk("", t.Name, "PRIMARY");
        await Each(conn, """
            SELECT k.CONSTRAINT_NAME, k.TABLE_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, r.DELETE_RULE, r.UPDATE_RULE
            FROM information_schema.KEY_COLUMN_USAGE k
            JOIN information_schema.REFERENTIAL_CONSTRAINTS r ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
            WHERE k.TABLE_SCHEMA = @db AND k.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """, r => AddFk(raw, r.GetString(0), "", r.GetString(1), r.GetString(2), "", r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)), ct, ("@db", db));
        await Each(conn, """
            SELECT s.INDEX_NAME, s.TABLE_NAME, s.NON_UNIQUE = 0, s.COLUMN_NAME FROM information_schema.STATISTICS s
            WHERE s.TABLE_SCHEMA = @db AND s.INDEX_NAME <> 'PRIMARY' ORDER BY s.TABLE_NAME, s.INDEX_NAME, s.SEQ_IN_INDEX
            """, r => AddIndex(raw, r.GetString(0), "", r.GetString(1), Flag(r, 2), r.GetString(3)), ct, ("@db", db));
        // MySQL creates an index for every foreign key; those are shown as relations, not as separate indexes
        raw.Indexes.RemoveAll(i => raw.Fks.Any(f => f.Name == i.Name && f.Table == i.Table));
        await Each(conn, "SELECT TABLE_NAME, VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME",
            r => raw.Views.Add(new DbsView("", r.GetString(0), Str(r, 1))), ct, ("@db", db));
        await Each(conn, "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @db ORDER BY ROUTINE_NAME",
            r => raw.Routines.Add(new DbsRoutine("", r.GetString(0), r.GetString(1) == "PROCEDURE" ? "Procedure" : "Function")), ct, ("@db", db));
        return raw;
    }

    // ------------------------------------------------------------------ Oracle

    private static async Task<Raw> Oracle(DbConnection conn, string owner, CancellationToken ct)
    {
        var raw = new Raw();
        const string columns = """
            SELECT c.OWNER, c.TABLE_NAME, t.NUM_ROWS, c.COLUMN_NAME,
                   CASE WHEN c.DATA_TYPE IN ('VARCHAR2','NVARCHAR2','CHAR','NCHAR','RAW') THEN c.DATA_TYPE || '(' || c.CHAR_LENGTH || ')'
                        WHEN c.DATA_TYPE = 'NUMBER' AND c.DATA_PRECISION IS NOT NULL THEN 'NUMBER(' || c.DATA_PRECISION || ',' || NVL(c.DATA_SCALE, 0) || ')'
                        ELSE c.DATA_TYPE END,
                   CASE WHEN c.NULLABLE = 'Y' THEN 1 ELSE 0 END, c.DATA_DEFAULT, {IDENTITY}, {VIRTUAL}, c.COLUMN_ID, c.CHAR_LENGTH
            FROM ALL_TAB_COLS c JOIN ALL_TABLES t ON t.OWNER = c.OWNER AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.OWNER = :owner AND c.HIDDEN_COLUMN = 'NO' AND c.TABLE_NAME NOT LIKE 'BIN$%' AND t.NESTED = 'NO'
            ORDER BY c.TABLE_NAME, c.COLUMN_ID
            """;
        void Column(DbDataReader r)
        {
            var t = raw.Table(r.GetString(0), r.GetString(1), Long(r, 2));
            var def = Str(r, 6)?.Trim();
            var identity = Flag(r, 7);
            var type = r.GetString(4);
            t.Columns.Add(new DbsColumn(r.GetString(3), type, Flag(r, 5), identity ? null : def, identity, false, Flag(r, 8), Convert.ToInt32(r.GetValue(9)),
                type.Contains("CHAR") ? Int(r, 10) : null));
        }
        try { await Each(conn, columns.Replace("{IDENTITY}", "CASE WHEN c.IDENTITY_COLUMN = 'YES' THEN 1 ELSE 0 END").Replace("{VIRTUAL}", "CASE WHEN c.VIRTUAL_COLUMN = 'YES' THEN 1 ELSE 0 END"), Column, ct, (":owner", owner)); }
        catch (OracleException) { await Each(conn, columns.Replace("{IDENTITY}", "0").Replace("{VIRTUAL}", "0"), Column, ct, (":owner", owner)); } // Oracle 11g
        await Each(conn, """
            SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME, cc.COLUMN_NAME FROM ALL_CONSTRAINTS c
            JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            WHERE c.OWNER = :owner AND c.CONSTRAINT_TYPE = 'P' ORDER BY c.TABLE_NAME, cc.POSITION
            """, r => MarkPk(raw, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, (":owner", owner));
        await Each(conn, """
            SELECT c.CONSTRAINT_NAME, c.OWNER, c.TABLE_NAME, cc.COLUMN_NAME, r.OWNER, r.TABLE_NAME, rc.COLUMN_NAME, c.DELETE_RULE
            FROM ALL_CONSTRAINTS c
            JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            JOIN ALL_CONSTRAINTS r ON r.OWNER = c.R_OWNER AND r.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME
            JOIN ALL_CONS_COLUMNS rc ON rc.OWNER = r.OWNER AND rc.CONSTRAINT_NAME = r.CONSTRAINT_NAME AND rc.POSITION = cc.POSITION
            WHERE c.OWNER = :owner AND c.CONSTRAINT_TYPE = 'R' ORDER BY c.CONSTRAINT_NAME, cc.POSITION
            """, r => AddFk(raw, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), "NO ACTION"), ct, (":owner", owner));
        await Each(conn, """
            SELECT i.INDEX_NAME, i.TABLE_OWNER, i.TABLE_NAME, CASE WHEN i.UNIQUENESS = 'UNIQUE' THEN 1 ELSE 0 END, ic.COLUMN_NAME
            FROM ALL_INDEXES i JOIN ALL_IND_COLUMNS ic ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME
            WHERE i.TABLE_OWNER = :owner AND i.INDEX_TYPE NOT IN ('LOB')
              AND NOT EXISTS (SELECT 1 FROM ALL_CONSTRAINTS k WHERE k.OWNER = i.TABLE_OWNER AND k.INDEX_NAME = i.INDEX_NAME AND k.CONSTRAINT_TYPE IN ('P','U'))
            ORDER BY i.TABLE_NAME, i.INDEX_NAME, ic.COLUMN_POSITION
            """, r => AddIndex(raw, r.GetString(0), r.GetString(1), r.GetString(2), Flag(r, 3), r.GetString(4)), ct, (":owner", owner));
        await Each(conn, "SELECT OWNER, VIEW_NAME, TEXT FROM ALL_VIEWS WHERE OWNER = :owner ORDER BY VIEW_NAME",
            r => raw.Views.Add(new DbsView(r.GetString(0), r.GetString(1), Str(r, 2))), ct, (":owner", owner));
        await Each(conn, "SELECT OWNER, OBJECT_NAME, OBJECT_TYPE FROM ALL_OBJECTS WHERE OWNER = :owner AND OBJECT_TYPE IN ('PROCEDURE','FUNCTION','PACKAGE') ORDER BY OBJECT_NAME",
            r => raw.Routines.Add(new DbsRoutine(r.GetString(0), r.GetString(1), r.GetString(2) switch { "PROCEDURE" => "Procedure", "PACKAGE" => "Package", _ => "Function" })), ct, (":owner", owner));
        return raw;
    }

    // ------------------------------------------------------------------ SQLite

    private static async Task<Raw> Sqlite(DbConnection conn, CancellationToken ct)
    {
        var raw = new Raw();
        var tables = new List<(string Name, string Sql)>();
        await Each(conn, "SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name", r =>
        {
            if (r.GetString(1) == "view") raw.Views.Add(new DbsView("", r.GetString(0), Str(r, 2)));
            else tables.Add((r.GetString(0), Str(r, 2) ?? ""));
        }, ct);
        foreach (var (name, sql) in tables)
        {
            var q = "\"" + name.Replace("\"", "\"\"") + "\"";
            long? rows = null;
            try { await Each(conn, $"SELECT COUNT(*) FROM {q}", r => rows = Long(r, 0), ct); } catch { /* ignore */ }
            var t = raw.Table("", name, rows);
            var pkCount = 0;
            var cols = new List<(string Name, string Type, bool NotNull, string? Def, int Pk, int Cid)>();
            await Each(conn, $"PRAGMA table_info({q})", r => cols.Add((r.GetString(1), Str(r, 2) ?? "", Flag(r, 3), Str(r, 4), Convert.ToInt32(r.GetValue(5)), Convert.ToInt32(r.GetValue(0)))), ct);
            pkCount = cols.Count(c => c.Pk > 0);
            var autoinc = sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase);
            foreach (var c in cols)
            {
                var identity = c.Pk > 0 && pkCount == 1 && c.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase);
                t.Columns.Add(new DbsColumn(c.Name, c.Type, !c.NotNull && c.Pk == 0, c.Def, identity && (autoinc || true), c.Pk > 0, false, c.Cid + 1, null));
            }
            foreach (var c in cols.Where(c => c.Pk > 0).OrderBy(c => c.Pk)) t.PrimaryKey.Add(c.Name);
            if (t.PrimaryKey.Count > 0) raw.SetPk("", name, "PRIMARY KEY");
            await Each(conn, $"PRAGMA foreign_key_list({q})", r =>
                AddFk(raw, $"fk_{name}_{r.GetValue(0)}", "", name, r.GetString(3), "", r.GetString(2), Str(r, 4) ?? "", Str(r, 6) ?? "", Str(r, 5) ?? ""), ct);
            var indexes = new List<(string Name, bool Unique, string Origin)>();
            await Each(conn, $"PRAGMA index_list({q})", r => indexes.Add((r.GetString(1), Flag(r, 2), Str(r, 3) ?? "c")), ct);
            foreach (var ix in indexes.Where(i => i.Origin == "c"))
                await Each(conn, $"PRAGMA index_info(\"{ix.Name.Replace("\"", "\"\"")}\")", r => AddIndex(raw, ix.Name, "", name, ix.Unique, Str(r, 2) ?? "?"), ct);
        }
        return raw;
    }

    // ------------------------------------------------------------------ Routine definitions (loaded on demand)

    public static async Task<string> RoutineDefinitionAsync(DbConnection conn, DbConnectionInfo info, string schema, string name, string kind, CancellationToken ct)
    {
        string? text = null;
        switch (info.Provider)
        {
            case DbProvider.SqlServer:
                await Each(conn, "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))", r => text = Str(r, 0), ct, ("@n", $"[{schema}].[{name}]"));
                break;
            case DbProvider.PostgreSql:
                await Each(conn, "SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @s AND p.proname = @n LIMIT 1",
                    r => text = Str(r, 0), ct, ("@s", schema), ("@n", name));
                break;
            case DbProvider.MySql:
                var what = kind == "Procedure" ? "PROCEDURE" : "FUNCTION";
                await Each(conn, $"SHOW CREATE {what} `{name.Replace("`", "``")}`", r => text = Str(r, 2), ct);
                break;
            case DbProvider.Oracle:
                var lines = new List<string>();
                await Each(conn, "SELECT TEXT FROM ALL_SOURCE WHERE OWNER = :o AND NAME = :n AND TYPE = :t ORDER BY LINE",
                    r => lines.Add(Str(r, 0) ?? ""), ct, (":o", schema), (":n", name), (":t", kind.ToUpperInvariant()));
                text = string.Concat(lines);
                break;
        }
        return string.IsNullOrWhiteSpace(text) ? "(The definition isn't available. It may be encrypted, or you may not have permission to see it.)" : text;
    }
}
