using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services.Db;

public sealed partial class SqlDialect
{
    /// <summary>Changes that can't run inside a transaction on some databases.</summary>
    public static bool NeedsNoTransaction(string kind) => kind is "createDatabase" or "dropDatabase" or "maintenance" or "createUser" or "dropUser";

    /// <summary>Changes that run against the server rather than one database.</summary>
    public static bool IsServerLevel(string kind) => kind is "createDatabase" or "dropDatabase";

    private string Principal(string name) => Provider switch
    {
        DbProvider.MySql => name.Contains('@') ? $"'{L(name[..name.LastIndexOf('@')])}'@'{L(name[(name.LastIndexOf('@') + 1)..])}'" : $"'{L(name)}'@'%'",
        _ => Quote(name),
    };

    private static string L(string? s) => (s ?? "").Replace("'", "''");

    private List<string> DdlMore(DdlChange ch, DbSchema? schema)
    {
        var t = Table(ch.Schema, ch.Table ?? "");
        string Need(string? v, string what) => string.IsNullOrWhiteSpace(v) ? throw new StudioException($"Enter {what}.") : v.Trim();
        StudioException No(string what) => new($"{Name} can't {what}.");

        switch (ch.Kind)
        {
            // ---------------------------------------------- code objects written by the user
            case "script":
            {
                var sql = Need(ch.Sql, "the definition").Trim();
                // A "/" line ends a PL/SQL block in Oracle scripts; the database itself doesn't want it
                if (sql.EndsWith('/')) sql = sql[..^1].TrimEnd();
                // MySQL scripts often switch the statement delimiter; the server needs the plain statement
                if (Provider == DbProvider.MySql)
                {
                    sql = System.Text.RegularExpressions.Regex.Replace(sql, @"^\s*DELIMITER\s+\S+\s*$", "", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    if (sql.EndsWith("$$")) sql = sql[..^2].TrimEnd();
                    else if (sql.EndsWith("//")) sql = sql[..^2].TrimEnd();
                }
                // PL/SQL blocks must keep their final "END;", plain statements must not end with ";"
                var plsql = Provider == DbProvider.Oracle && System.Text.RegularExpressions.Regex.IsMatch(sql, @"\bEND(\s+[A-Za-z0-9_$#""]+)?\s*;$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!plsql && Provider != DbProvider.PostgreSql) sql = sql.TrimEnd(';').TrimEnd();
                return [sql];
            }
            case "replaceRoutine":
            {
                // MySQL can't replace a routine in place: drop it, then create it again
                var kind = (ch.OldName ?? "PROCEDURE").ToUpperInvariant();
                return [$"DROP {kind} IF EXISTS {Quote(Need(ch.Table, "the name"))}", Need(ch.Sql, "the definition")];
            }
            case "alterView":
            {
                var body = Need(ch.Sql, "the query").TrimEnd().TrimEnd(';');
                return Provider switch
                {
                    DbProvider.SqlServer => [$"CREATE OR ALTER VIEW {t} AS\n{body}"],
                    // PostgreSQL can't remove or reorder a view's columns in place; both steps run in one transaction
                    DbProvider.Sqlite or DbProvider.PostgreSql => [$"DROP VIEW {t}", $"CREATE VIEW {t} AS\n{body}"],
                    _ => [$"CREATE OR REPLACE VIEW {t} AS\n{body}"],
                };
            }
            case "renameView":
                return Provider switch
                {
                    DbProvider.SqlServer => [$"EXEC sp_rename '{L(ch.Schema)}.{L(ch.Table)}', '{L(ch.NewName)}'"],
                    DbProvider.PostgreSql => [$"ALTER VIEW {t} RENAME TO {Quote(Need(ch.NewName, "a name"))}"],
                    DbProvider.MySql => [$"RENAME TABLE {t} TO {Quote(Need(ch.NewName, "a name"))}"],
                    DbProvider.Oracle => [$"RENAME {Quote(ch.Table ?? "")} TO {Quote(Need(ch.NewName, "a name"))}"],
                    _ => throw No("rename a view; create a new one and delete the old one"),
                };

            // ---------------------------------------------- tables
            case "copyTable":
            {
                var source = schema?.Tables.FirstOrDefault(x => x.Schema == (ch.Schema ?? "") && x.Name == ch.Table) ?? throw new StudioException("That table no longer exists.");
                var name = Need(ch.NewName, "a name for the copy");
                var defs = source.Columns.Where(c => !c.Computed).Select(c => new DbsColumnDef(c.Name, c.Type, c.Nullable, c.Default, c.Identity, c.PrimaryKey)).ToList();
                var list = Ddl(new DdlChange("createTable", ch.Schema, name, Columns: defs), null);
                if (ch.WithData)
                {
                    var cols = string.Join(", ", defs.Select(c => Quote(c.Name)));
                    var target = Table(ch.Schema, name);
                    var insert = $"INSERT INTO {target} ({cols}) SELECT {cols} FROM {t}";
                    list.Add(Provider == DbProvider.SqlServer && defs.Any(c => c.Identity) ? $"SET IDENTITY_INSERT {target} ON; {insert}; SET IDENTITY_INSERT {target} OFF" : insert);
                }
                return list;
            }
            case "addUnique":
            {
                if (Provider == DbProvider.Sqlite) throw No("add a rule to an existing table; add a unique index instead");
                var cols = ch.KeyColumns is { Count: > 0 } k ? k : throw new StudioException("Pick at least one column.");
                return [$"ALTER TABLE {t} ADD CONSTRAINT {Quote(ch.ConstraintName ?? $"UQ_{ch.Table}_{string.Join("_", cols)}")} UNIQUE ({string.Join(", ", cols.Select(Quote))})"];
            }
            case "addCheck":
                if (Provider == DbProvider.Sqlite) throw No("add a rule to an existing table");
                return [$"ALTER TABLE {t} ADD CONSTRAINT {Quote(ch.ConstraintName ?? $"CK_{ch.Table}")} CHECK ({Need(ch.Sql, "the rule")})"];
            case "dropConstraint":
                if (Provider == DbProvider.Sqlite) throw No("remove a rule from an existing table");
                return [Provider == DbProvider.MySql && ch.Unique ? $"ALTER TABLE {t} DROP INDEX {Quote(Need(ch.ConstraintName, "the name"))}"
                    : Provider == DbProvider.MySql ? $"ALTER TABLE {t} DROP CHECK {Quote(Need(ch.ConstraintName, "the name"))}"
                    : $"ALTER TABLE {t} DROP CONSTRAINT {Quote(Need(ch.ConstraintName, "the name"))}"];

            // ---------------------------------------------- triggers
            case "dropTrigger":
            {
                var n = Need(ch.ConstraintName, "the trigger");
                return [Provider switch
                {
                    DbProvider.SqlServer or DbProvider.Oracle => $"DROP TRIGGER {Table(ch.Schema, n)}",
                    DbProvider.PostgreSql => $"DROP TRIGGER {Quote(n)} ON {t}",
                    _ => $"DROP TRIGGER {Quote(n)}",
                }];
            }
            case "toggleTrigger":
            {
                var n = Need(ch.ConstraintName, "the trigger");
                var on = ch.Enabled ? "ENABLE" : "DISABLE";
                return [Provider switch
                {
                    DbProvider.SqlServer => $"{on} TRIGGER {Table(ch.Schema, n)} ON {t}",
                    DbProvider.PostgreSql => $"ALTER TABLE {t} {on} TRIGGER {Quote(n)}",
                    DbProvider.Oracle => $"ALTER TRIGGER {Table(ch.Schema, n)} {on}",
                    _ => throw No("switch triggers off; delete it and create it again instead"),
                }];
            }

            // ---------------------------------------------- sequences
            case "createSequence":
            {
                if (Provider is DbProvider.MySql or DbProvider.Sqlite) throw No("use sequences; use an auto-number column instead");
                var start = long.TryParse(ch.Sql, out var s0) ? s0 : 1;
                return [$"CREATE SEQUENCE {Table(ch.Schema, Need(ch.NewName, "a name"))} START WITH {start} INCREMENT BY 1"];
            }
            case "dropSequence":
                return [$"DROP SEQUENCE {Table(ch.Schema, Need(ch.Table, "the sequence"))}"];
            case "restartSequence":
            {
                var v = long.TryParse(ch.Sql, out var n) ? n : throw new StudioException("Enter the number to start from.");
                return [Provider == DbProvider.Oracle ? $"ALTER SEQUENCE {Table(ch.Schema, ch.Table ?? "")} RESTART START WITH {v}" : $"ALTER SEQUENCE {Table(ch.Schema, ch.Table ?? "")} RESTART WITH {v}"];
            }

            // ---------------------------------------------- schemas & databases
            case "dropSchema":
                if (Provider is not (DbProvider.SqlServer or DbProvider.PostgreSql)) throw No("use schemas");
                return [$"DROP SCHEMA {Quote(Need(ch.OldName, "the schema"))}"];
            case "createDatabase":
            {
                var n = Need(ch.NewName, "a name");
                return Provider switch
                {
                    DbProvider.SqlServer or DbProvider.PostgreSql or DbProvider.MySql => [$"CREATE DATABASE {Quote(n)}"],
                    DbProvider.Oracle => throw new StudioException("On Oracle, a schema is a user: create a user instead."),
                    _ => throw new StudioException("A SQLite database is a file: use “New SQLite file” when connecting."),
                };
            }
            case "dropDatabase":
            {
                var n = Need(ch.OldName, "the database");
                return Provider switch
                {
                    DbProvider.SqlServer => [$"ALTER DATABASE {Quote(n)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE", $"DROP DATABASE {Quote(n)}"],
                    DbProvider.PostgreSql => [$"DROP DATABASE {Quote(n)} WITH (FORCE)"],
                    DbProvider.MySql => [$"DROP DATABASE {Quote(n)}"],
                    _ => throw No("delete databases from here"),
                };
            }

            // ---------------------------------------------- users & permissions
            case "createUser":
            {
                var n = Need(ch.Principal, "a user name");
                var pw = Need(ch.Password, "a password");
                return Provider switch
                {
                    DbProvider.SqlServer => [$"CREATE LOGIN {Quote(n)} WITH PASSWORD = N'{L(pw)}'", $"CREATE USER {Quote(n)} FOR LOGIN {Quote(n)}"],
                    DbProvider.PostgreSql => [$"CREATE ROLE {Quote(n)} LOGIN PASSWORD '{L(pw)}'"],
                    DbProvider.MySql => [$"CREATE USER {Principal(n)} IDENTIFIED BY '{L(pw)}'"],
                    DbProvider.Oracle => [$"CREATE USER {Quote(n)} IDENTIFIED BY \"{pw.Replace("\"", "")}\"", $"GRANT CREATE SESSION TO {Quote(n)}"],
                    _ => throw No("have users"),
                };
            }
            case "createRole":
            {
                var n = Need(ch.Principal, "a role name");
                return Provider switch
                {
                    DbProvider.SqlServer => [$"CREATE ROLE {Quote(n)}"],
                    DbProvider.PostgreSql => [$"CREATE ROLE {Quote(n)} NOLOGIN"],
                    DbProvider.MySql => [$"CREATE ROLE {Principal(n)}"],
                    DbProvider.Oracle => [$"CREATE ROLE {Quote(n)}"],
                    _ => throw No("have roles"),
                };
            }
            case "dropUser":
            {
                var n = Need(ch.Principal, "the user");
                return Provider switch
                {
                    DbProvider.SqlServer => ch.OldName == "role" ? [$"DROP ROLE {Quote(n)}"]
                        : [$"DROP USER {Quote(n)}", $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{L(n)}' AND type = 'S') DROP LOGIN {Quote(n)}"],
                    DbProvider.PostgreSql => [$"DROP ROLE {Quote(n)}"],
                    DbProvider.MySql => [ch.OldName == "role" ? $"DROP ROLE {Principal(n)}" : $"DROP USER {Principal(n)}"],
                    DbProvider.Oracle => [ch.OldName == "role" ? $"DROP ROLE {Quote(n)}" : $"DROP USER {Quote(n)}"],
                    _ => throw No("have users"),
                };
            }
            case "addMember":
            {
                var role = Need(ch.OldName, "the role");
                var user = Need(ch.Principal, "the user");
                return Provider switch
                {
                    DbProvider.SqlServer => [$"ALTER ROLE {Quote(role)} ADD MEMBER {Quote(user)}"],
                    DbProvider.MySql => [$"GRANT {Principal(role)} TO {Principal(user)}"],
                    _ => [$"GRANT {Quote(role)} TO {Principal(user)}"],
                };
            }
            case "grant" or "revoke":
            {
                if (Provider == DbProvider.Sqlite) throw No("have permissions");
                var privs = ch.Privileges is { Count: > 0 } p ? p.Select(x => x.ToUpperInvariant()).Where(x => x is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "REFERENCES" or "EXECUTE" or "ALL").ToList() : [];
                if (privs.Count == 0) throw new StudioException("Pick at least one permission.");
                var on = ch.Table is { Length: > 0 } ? (Provider == DbProvider.MySql && string.IsNullOrEmpty(ch.Schema) ? t : t) : throw new StudioException("Pick a table, view or procedure.");
                var who = Principal(Need(ch.Principal, "a user or role"));
                var list = string.Join(", ", privs.Select(x => x == "ALL" ? "ALL PRIVILEGES" : x));
                if (ch.Unique && Provider == DbProvider.MySql) on = $"PROCEDURE {on}";
                return [ch.Kind == "grant" ? $"GRANT {list} ON {on} TO {who}" : $"REVOKE {list} ON {on} FROM {who}"];
            }

            // ---------------------------------------------- upkeep
            case "maintenance":
            {
                var op = (ch.Sql ?? "analyze").ToLowerInvariant();
                var tables = ch.Table is { Length: > 0 }
                    ? [(ch.Schema ?? "", ch.Table)]
                    : (schema?.Tables.Select(x => (x.Schema, x.Name)).ToList() ?? []);
                return (Provider, op) switch
                {
                    (DbProvider.SqlServer, "analyze") => ch.Table is { Length: > 0 } ? [$"UPDATE STATISTICS {t}"] : ["EXEC sp_updatestats"],
                    (DbProvider.SqlServer, "rebuild") => tables.Select(x => $"ALTER INDEX ALL ON {Table(x.Item1, x.Item2)} REBUILD").ToList(),
                    (DbProvider.SqlServer, _) => tables.Select(x => $"ALTER INDEX ALL ON {Table(x.Item1, x.Item2)} REORGANIZE").ToList(),
                    (DbProvider.PostgreSql, "analyze") => [ch.Table is { Length: > 0 } ? $"ANALYZE {t}" : "ANALYZE"],
                    (DbProvider.PostgreSql, "rebuild") => tables.Select(x => $"REINDEX TABLE {Table(x.Item1, x.Item2)}").ToList(),
                    (DbProvider.PostgreSql, _) => [ch.Table is { Length: > 0 } ? $"VACUUM (ANALYZE) {t}" : "VACUUM (ANALYZE)"],
                    (DbProvider.MySql, "analyze") => tables.Select(x => $"ANALYZE TABLE {Table(x.Item1, x.Item2)}").ToList(),
                    (DbProvider.MySql, _) => tables.Select(x => $"OPTIMIZE TABLE {Table(x.Item1, x.Item2)}").ToList(),
                    (DbProvider.Oracle, "rebuild") => (schema?.Indexes.Where(i => tables.Any(x => x.Item1 == i.Schema && x.Item2 == i.Table)).Select(i => $"ALTER INDEX {Table(i.Schema, i.Name)} REBUILD").ToList()) is { Count: > 0 } ix ? ix : throw new StudioException("This table has no extra indexes to rebuild (its key is kept in order automatically)."),
                    (DbProvider.Oracle, _) => ch.Table is { Length: > 0 }
                        ? [$"BEGIN DBMS_STATS.GATHER_TABLE_STATS('{L(ch.Schema)}', '{L(ch.Table)}'); END;"]
                        : [$"BEGIN DBMS_STATS.GATHER_SCHEMA_STATS('{L(schema?.Database)}'); END;"],
                    (_, "analyze") => [ch.Table is { Length: > 0 } ? $"ANALYZE {t}" : "ANALYZE"],
                    (_, "rebuild") => [ch.Table is { Length: > 0 } ? $"REINDEX {t}" : "REINDEX"],
                    _ => ["VACUUM"],
                };
            }
            default:
                throw new StudioException("That change isn't supported.");
        }
    }

    private string Name => Provider switch
    {
        DbProvider.SqlServer => "SQL Server", DbProvider.PostgreSql => "PostgreSQL", DbProvider.MySql => "MySQL", DbProvider.Oracle => "Oracle", _ => "SQLite",
    };
}
