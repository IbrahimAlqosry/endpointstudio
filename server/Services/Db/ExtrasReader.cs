using System.Data.Common;
using System.Text.RegularExpressions;
using QosriStudio.Server.Models;
using static QosriStudio.Server.Services.Db.SchemaReader;

namespace QosriStudio.Server.Services.Db;

/// <summary>Reads the rest of a database: triggers, sequences, rules (unique/check), users and roles, permissions, routine parameters.</summary>
public static class ExtrasReader
{
    public static async Task<DbExtras> ReadAsync(DbConnection conn, DbConnectionInfo info, CancellationToken ct)
    {
        var p = info.Provider;
        var owner = (info.Database ?? "").ToUpperInvariant();
        var db = info.Database ?? "";
        var triggers = new List<DbsTrigger>();
        var sequences = new List<DbsSequence>();
        var constraints = new List<DbsConstraint>();
        var principals = new List<DbsPrincipal>();
        var notes = new List<string>();

        async Task Try(string what, Func<Task> work)
        {
            try { await work(); }
            catch (DbException) { notes.Add($"Your sign-in can't see the {what}."); }
        }

        void AddConstraint(string schema, string table, string name, string kind, string? column, string? def)
        {
            var i = constraints.FindIndex(c => c.Schema == schema && c.Table == table && c.Name == name);
            if (i >= 0) { if (column is not null) constraints[i].Columns.Add(column); return; }
            constraints.Add(new DbsConstraint(schema, table, name, kind, column is null ? [] : [column], def));
        }

        switch (p)
        {
            case DbProvider.SqlServer:
                await Try("triggers", () => Each(conn, """
                    SELECT s.name, o.name, t.name, CASE WHEN t.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END,
                           STUFF((SELECT ', ' + e.type_desc FROM sys.trigger_events e WHERE e.object_id = t.object_id FOR XML PATH('')), 1, 2, ''), t.is_disabled
                    FROM sys.triggers t JOIN sys.objects o ON o.object_id = t.parent_id JOIN sys.schemas s ON s.schema_id = o.schema_id
                    WHERE t.parent_class = 1 ORDER BY s.name, o.name, t.name
                    """, r => triggers.Add(new DbsTrigger(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4), !Flag(r, 5))), ct));
                await Try("sequences", () => Each(conn, "SELECT SCHEMA_NAME(schema_id), name, CONVERT(nvarchar(50), current_value), CONVERT(nvarchar(50), increment) FROM sys.sequences ORDER BY 1, 2",
                    r => sequences.Add(new DbsSequence(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3))), ct));
                await Try("rules", () => Each(conn, """
                    SELECT s.name, t.name, k.name, c.name FROM sys.key_constraints k
                    JOIN sys.tables t ON t.object_id = k.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
                    JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE k.type = 'UQ' ORDER BY s.name, t.name, k.name, ic.key_ordinal
                    """, r => AddConstraint(r.GetString(0), r.GetString(1), r.GetString(2), "unique", r.GetString(3), null), ct));
                await Try("rules", () => Each(conn, """
                    SELECT s.name, t.name, k.name, k.definition FROM sys.check_constraints k
                    JOIN sys.tables t ON t.object_id = k.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id ORDER BY 1, 2, 3
                    """, r => AddConstraint(r.GetString(0), r.GetString(1), r.GetString(2), "check", null, Str(r, 3)), ct));
                await Try("users and roles", () => Each(conn, """
                    SELECT name, type, CASE WHEN type IN ('S','U','E','X') AND authentication_type <> 0 THEN 1 ELSE 0 END
                    FROM sys.database_principals
                    WHERE is_fixed_role = 0 AND name NOT IN ('public','guest','INFORMATION_SCHEMA','sys') AND type IN ('S','U','G','R','E','X')
                    ORDER BY type, name
                    """, r => principals.Add(new DbsPrincipal(r.GetString(0), r.GetString(1).Trim() == "R" ? "role" : "user", Flag(r, 2))), ct));
                break;

            case DbProvider.PostgreSql:
                await Try("triggers", () => Each(conn, """
                    SELECT n.nspname, c.relname, t.tgname,
                           CASE WHEN t.tgtype & 2 = 2 THEN 'BEFORE' WHEN t.tgtype & 64 = 64 THEN 'INSTEAD OF' ELSE 'AFTER' END,
                           concat_ws(', ', CASE WHEN t.tgtype & 4 = 4 THEN 'INSERT' END, CASE WHEN t.tgtype & 16 = 16 THEN 'UPDATE' END, CASE WHEN t.tgtype & 8 = 8 THEN 'DELETE' END, CASE WHEN t.tgtype & 32 = 32 THEN 'TRUNCATE' END),
                           t.tgenabled <> 'D'
                    FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE NOT t.tgisinternal ORDER BY 1, 2, 3
                    """, r => triggers.Add(new DbsTrigger(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4), Convert.ToBoolean(r.GetValue(5)))), ct));
                await Try("sequences", () => Each(conn, "SELECT schemaname, sequencename, last_value::text, increment_by::text FROM pg_sequences WHERE schemaname NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2",
                    r => sequences.Add(new DbsSequence(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3))), ct));
                await Try("rules", () => Each(conn, """
                    SELECT n.nspname, c.relname, con.conname, CASE con.contype WHEN 'u' THEN 'unique' ELSE 'check' END, a.attname, pg_get_constraintdef(con.oid)
                    FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                    LEFT JOIN LATERAL unnest(con.conkey) WITH ORDINALITY k(attnum, ord) ON con.contype = 'u'
                    LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
                    WHERE con.contype IN ('u','c') AND n.nspname NOT IN ('pg_catalog','information_schema')
                    ORDER BY 1, 2, 3, k.ord
                    """, r => AddConstraint(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), r.GetString(3) == "check" ? Regex.Replace(Str(r, 5) ?? "", @"^CHECK\s*", "") : null), ct));
                await Try("users and roles", () => Each(conn, "SELECT rolname, rolcanlogin FROM pg_roles WHERE rolname NOT LIKE 'pg\\_%' ORDER BY rolcanlogin DESC, rolname",
                    r => principals.Add(new DbsPrincipal(r.GetString(0), Convert.ToBoolean(r.GetValue(1)) ? "user" : "role", Convert.ToBoolean(r.GetValue(1)))), ct));
                break;

            case DbProvider.MySql:
                await Try("triggers", () => Each(conn, "SELECT EVENT_OBJECT_TABLE, TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = @db ORDER BY 1, 2",
                    r => triggers.Add(new DbsTrigger("", r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3), true)), ct, ("@db", db)));
                await Try("rules", () => Each(conn, """
                    SELECT c.TABLE_NAME, c.CONSTRAINT_NAME, k.COLUMN_NAME FROM information_schema.TABLE_CONSTRAINTS c
                    JOIN information_schema.KEY_COLUMN_USAGE k ON k.CONSTRAINT_SCHEMA = c.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND k.TABLE_NAME = c.TABLE_NAME
                    WHERE c.TABLE_SCHEMA = @db AND c.CONSTRAINT_TYPE = 'UNIQUE' ORDER BY 1, 2, k.ORDINAL_POSITION
                    """, r => AddConstraint("", r.GetString(0), r.GetString(1), "unique", r.GetString(2), null), ct, ("@db", db)));
                try
                {
                    await Each(conn, """
                        SELECT t.TABLE_NAME, c.CONSTRAINT_NAME, c.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS c
                        JOIN information_schema.TABLE_CONSTRAINTS t ON t.CONSTRAINT_SCHEMA = c.CONSTRAINT_SCHEMA AND t.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                        WHERE c.CONSTRAINT_SCHEMA = @db ORDER BY 1, 2
                        """, r => AddConstraint("", r.GetString(0), r.GetString(1), "check", null, Str(r, 2)), ct, ("@db", db));
                }
                catch (DbException) { /* MySQL before 8.0.16 has no check rules */ }
                await Try("users", () => Each(conn, "SELECT CONCAT(User, '@', Host) FROM mysql.user WHERE User NOT IN ('mysql.sys','mysql.session','mysql.infoschema') ORDER BY User",
                    r => principals.Add(new DbsPrincipal(r.GetString(0), "user", true)), ct));
                break;

            case DbProvider.Oracle:
                await Try("triggers", () => Each(conn, "SELECT OWNER, TABLE_NAME, TRIGGER_NAME, TRIGGER_TYPE, TRIGGERING_EVENT, STATUS FROM ALL_TRIGGERS WHERE OWNER = :o AND TABLE_NAME IS NOT NULL ORDER BY TABLE_NAME, TRIGGER_NAME",
                    r => triggers.Add(new DbsTrigger(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), Str(r, 4), Str(r, 5) == "ENABLED")), ct, (":o", owner)));
                await Try("sequences", () => Each(conn, "SELECT SEQUENCE_OWNER, SEQUENCE_NAME, TO_CHAR(LAST_NUMBER), TO_CHAR(INCREMENT_BY) FROM ALL_SEQUENCES WHERE SEQUENCE_OWNER = :o AND SEQUENCE_NAME NOT LIKE 'ISEQ$$%' ORDER BY 2",
                    r => sequences.Add(new DbsSequence(r.GetString(0), r.GetString(1), Str(r, 2), Str(r, 3))), ct, (":o", owner)));
                await Try("rules", () => Each(conn, """
                    SELECT c.OWNER, c.TABLE_NAME, c.CONSTRAINT_NAME, c.CONSTRAINT_TYPE, cc.COLUMN_NAME, c.SEARCH_CONDITION
                    FROM ALL_CONSTRAINTS c LEFT JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND c.CONSTRAINT_TYPE = 'U'
                    WHERE c.OWNER = :o AND c.CONSTRAINT_TYPE IN ('U','C') AND c.TABLE_NAME NOT LIKE 'BIN$%'
                    ORDER BY c.TABLE_NAME, c.CONSTRAINT_NAME, cc.POSITION
                    """, r =>
                {
                    var kind = Str(r, 3) == "U" ? "unique" : "check";
                    var cond = Str(r, 5);
                    // Oracle stores NOT NULL columns as check rules; those are shown as "required" instead
                    if (kind == "check" && cond is not null && Regex.IsMatch(cond, @"^""?[A-Za-z0-9_$#]+""?\s+IS\s+NOT\s+NULL$", RegexOptions.IgnoreCase)) return;
                    AddConstraint(r.GetString(0), r.GetString(1), r.GetString(2), kind, Str(r, 4), cond);
                }, ct, (":o", owner)));
                await Try("users", () => Each(conn, "SELECT USERNAME FROM ALL_USERS WHERE ORACLE_MAINTAINED = 'N' ORDER BY USERNAME",
                    r => principals.Add(new DbsPrincipal(r.GetString(0), "user", true)), ct));
                break;

            default: // SQLite
                await Try("triggers", () => Each(conn, "SELECT tbl_name, name FROM sqlite_master WHERE type = 'trigger' ORDER BY 1, 2",
                    r => triggers.Add(new DbsTrigger("", r.GetString(0), r.GetString(1), null, null, true)), ct));
                break;
        }

        var caps = new DbExtrasCaps(
            Triggers: true,
            Sequences: p is DbProvider.SqlServer or DbProvider.PostgreSql or DbProvider.Oracle,
            Users: p != DbProvider.Sqlite,
            Databases: p is DbProvider.SqlServer or DbProvider.PostgreSql or DbProvider.MySql,
            Backup: true,
            CheckConstraints: p != DbProvider.Sqlite);
        return new DbExtras(triggers, sequences, constraints, principals, notes.Distinct().ToList(), caps);
    }

    public static async Task<string> TriggerDefinitionAsync(DbConnection conn, DbConnectionInfo info, string schema, string table, string name, CancellationToken ct)
    {
        string? text = null;
        switch (info.Provider)
        {
            case DbProvider.SqlServer:
                await Each(conn, "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))", r => text = Str(r, 0), ct, ("@n", $"[{schema}].[{name}]"));
                break;
            case DbProvider.PostgreSql:
                await Each(conn, """
                    SELECT pg_get_triggerdef(t.oid, true) || E';\n\n-- Function it runs:\n' || pg_get_functiondef(t.tgfoid)
                    FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @s AND c.relname = @t AND t.tgname = @n
                    """, r => text = Str(r, 0), ct, ("@s", schema), ("@t", table), ("@n", name));
                break;
            case DbProvider.MySql:
                await Each(conn, $"SHOW CREATE TRIGGER `{name.Replace("`", "``")}`", r => text = Str(r, 2), ct);
                break;
            case DbProvider.Oracle:
                var lines = new List<string>();
                await Each(conn, "SELECT TEXT FROM ALL_SOURCE WHERE OWNER = :o AND NAME = :n AND TYPE = 'TRIGGER' ORDER BY LINE", r => lines.Add(Str(r, 0) ?? ""), ct, (":o", schema), (":n", name));
                text = lines.Count > 0 ? "CREATE OR REPLACE " + string.Concat(lines) : null;
                break;
            default:
                await Each(conn, "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = @n", r => text = Str(r, 0), ct, ("@n", name));
                break;
        }
        return text ?? "(The definition isn't available.)";
    }

    public static async Task<RoutineDetails> RoutineAsync(DbConnection conn, DbConnectionInfo info, string schema, string name, string kind, CancellationToken ct)
    {
        var definition = await RoutineDefinitionAsync(conn, info, schema, name, kind, ct);
        var ps = new List<RoutineParam>();
        string? returns = null;
        switch (info.Provider)
        {
            case DbProvider.SqlServer:
                await Each(conn, """
                    SELECT p.name, TYPE_NAME(p.user_type_id) +
                           CASE WHEN TYPE_NAME(p.user_type_id) IN ('nvarchar','nchar') THEN '(' + CASE WHEN p.max_length = -1 THEN 'max' ELSE CAST(p.max_length / 2 AS varchar) END + ')'
                                WHEN TYPE_NAME(p.user_type_id) IN ('varchar','char','varbinary') THEN '(' + CASE WHEN p.max_length = -1 THEN 'max' ELSE CAST(p.max_length AS varchar) END + ')'
                                WHEN TYPE_NAME(p.user_type_id) IN ('decimal','numeric') THEN '(' + CAST(p.precision AS varchar) + ',' + CAST(p.scale AS varchar) + ')' ELSE '' END,
                           p.is_output, p.parameter_id
                    FROM sys.parameters p WHERE p.object_id = OBJECT_ID(@n) ORDER BY p.parameter_id
                    """, r =>
                {
                    if (Convert.ToInt32(r.GetValue(3)) == 0) returns = r.GetString(1);
                    else ps.Add(new RoutineParam(r.GetString(0), r.GetString(1), Flag(r, 2) ? "OUT" : "IN"));
                }, ct, ("@n", $"[{schema}].[{name}]"));
                break;
            case DbProvider.PostgreSql:
                await Each(conn, """
                    SELECT COALESCE(p.parameter_name, '$' || p.ordinal_position), CASE WHEN p.data_type = 'USER-DEFINED' OR p.data_type = 'ARRAY' THEN p.udt_name ELSE p.data_type END, p.parameter_mode
                    FROM information_schema.routines r JOIN information_schema.parameters p ON p.specific_schema = r.specific_schema AND p.specific_name = r.specific_name
                    WHERE r.routine_schema = @s AND r.routine_name = @n AND p.parameter_mode IN ('IN','INOUT','OUT')
                    AND r.specific_name = (SELECT min(specific_name) FROM information_schema.routines WHERE routine_schema = @s AND routine_name = @n)
                    ORDER BY p.ordinal_position
                    """, r => ps.Add(new RoutineParam(r.GetString(0), r.GetString(1), Str(r, 2) ?? "IN")), ct, ("@s", schema), ("@n", name));
                await Each(conn, "SELECT data_type FROM information_schema.routines WHERE routine_schema = @s AND routine_name = @n LIMIT 1", r => returns = Str(r, 0), ct, ("@s", schema), ("@n", name));
                break;
            case DbProvider.MySql:
                await Each(conn, "SELECT PARAMETER_NAME, DTD_IDENTIFIER, PARAMETER_MODE, ORDINAL_POSITION FROM information_schema.PARAMETERS WHERE SPECIFIC_SCHEMA = @db AND SPECIFIC_NAME = @n ORDER BY ORDINAL_POSITION",
                    r =>
                    {
                        if (Convert.ToInt32(r.GetValue(3)) == 0) returns = Str(r, 1);
                        else ps.Add(new RoutineParam(Str(r, 0) ?? "", Str(r, 1) ?? "", Str(r, 2) ?? "IN"));
                    }, ct, ("@db", info.Database ?? ""), ("@n", name));
                break;
            case DbProvider.Oracle:
                await Each(conn, "SELECT ARGUMENT_NAME, DATA_TYPE, IN_OUT, POSITION FROM ALL_ARGUMENTS WHERE OWNER = :o AND OBJECT_NAME = :n AND PACKAGE_NAME IS NULL AND DATA_LEVEL = 0 ORDER BY POSITION",
                    r =>
                    {
                        if (r.IsDBNull(0)) returns = Str(r, 1);
                        else ps.Add(new RoutineParam(r.GetString(0), Str(r, 1) ?? "", (Str(r, 2) ?? "IN").Replace("/", "")));
                    }, ct, (":o", schema), (":n", name));
                break;
        }
        return new RoutineDetails(definition, Editable(info.Provider, definition, kind), ps, kind == "Procedure" ? null : returns);
    }

    /// <summary>The definition rewritten so that running it replaces the routine.</summary>
    public static string Editable(DbProvider p, string definition, string kind)
    {
        if (definition.StartsWith('(')) return definition; // not available
        return p switch
        {
            DbProvider.SqlServer => Regex.Replace(definition.TrimStart(), @"^\s*CREATE\s+(PROC(EDURE)?|FUNCTION|VIEW|TRIGGER)\b", m => "CREATE OR ALTER " + m.Groups[1].Value.ToUpperInvariant(), RegexOptions.IgnoreCase),
            DbProvider.Oracle => definition.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ? definition : "CREATE OR REPLACE " + definition.TrimStart(),
            DbProvider.MySql => Regex.Replace(definition, @"^\s*CREATE\s+DEFINER\s*=\s*\S+\s+", "CREATE ", RegexOptions.IgnoreCase),
            _ => definition,
        };
    }

    public static async Task<List<DbsGrant>> GrantsAsync(DbConnection conn, DbConnectionInfo info, string schema, string table, CancellationToken ct)
    {
        var list = new List<DbsGrant>();
        try
        {
            switch (info.Provider)
            {
                case DbProvider.SqlServer:
                    await Each(conn, """
                        SELECT pr.name, p.permission_name, p.state_desc FROM sys.database_permissions p
                        JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id
                        WHERE p.major_id = OBJECT_ID(@n) AND p.class = 1 ORDER BY 1, 2
                        """, r => list.Add(new DbsGrant(r.GetString(0), (Str(r, 2) == "DENY" ? "DENY " : "") + r.GetString(1), null)), ct, ("@n", $"[{schema}].[{table}]"));
                    break;
                case DbProvider.PostgreSql:
                    await Each(conn, "SELECT grantee, privilege_type, grantor FROM information_schema.role_table_grants WHERE table_schema = @s AND table_name = @t ORDER BY 1, 2",
                        r => list.Add(new DbsGrant(r.GetString(0), r.GetString(1), Str(r, 2))), ct, ("@s", schema), ("@t", table));
                    break;
                case DbProvider.MySql:
                    await Each(conn, "SELECT GRANTEE, PRIVILEGE_TYPE FROM information_schema.TABLE_PRIVILEGES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY 1, 2",
                        r => list.Add(new DbsGrant(r.GetString(0).Replace("'", ""), r.GetString(1), null)), ct, ("@db", info.Database ?? ""), ("@t", table));
                    break;
                case DbProvider.Oracle:
                    await Each(conn, "SELECT GRANTEE, PRIVILEGE, GRANTOR FROM ALL_TAB_PRIVS WHERE TABLE_SCHEMA = :o AND TABLE_NAME = :t ORDER BY 1, 2",
                        r => list.Add(new DbsGrant(r.GetString(0), r.GetString(1), Str(r, 2))), ct, (":o", schema), (":t", table));
                    break;
            }
        }
        catch (DbException) { /* no permission to see grants */ }
        return list;
    }
}
