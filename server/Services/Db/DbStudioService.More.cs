using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services.Db;

public partial class DbStudioService
{
    public Task<DbExtras> ExtrasAsync(DbRef r, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(r, ct);
        await using (conn) return await ExtrasReader.ReadAsync(conn, info, ct);
    });

    public Task<RoutineDetails> RoutineDetailsAsync(RoutineRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn) return await ExtrasReader.RoutineAsync(conn, info, req.Schema, req.Name, req.Kind, ct);
    });

    public Task<string> TriggerAsync(TriggerRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn) return await ExtrasReader.TriggerDefinitionAsync(conn, info, req.Schema, req.Table, req.Name, ct);
    });

    public Task<List<DbsGrant>> GrantsAsync(TableRefRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn) return await ExtrasReader.GrantsAsync(conn, info, req.Schema, req.Table, ct);
    });

    /// <summary>Runs a procedure or function with the values typed in, and shows what it returns.</summary>
    public Task<SqlResult> ExecuteRoutineAsync(ExecRoutineRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct, write: true);
        await using (conn)
        {
            var d = SqlDialect.For(info.Provider);
            var details = await ExtrasReader.RoutineAsync(conn, info, req.Schema, req.Name, req.Kind, ct);
            var inputs = details.Parameters.Where(p => p.Mode.Contains("IN", StringComparison.OrdinalIgnoreCase)).ToList();
            var name = d.Table(req.Schema, req.Name);
            var isProc = req.Kind == "Procedure";
            var args = new List<string>();
            await using var cmd = conn.CreateCommand();
            if (cmd is OracleCommand oc) oc.BindByName = true;
            for (var i = 0; i < inputs.Count; i++)
            {
                var p = inputs[i];
                var pn = d.Param(i);
                req.Values.TryGetValue(p.Name, out var v);
                var param = cmd.CreateParameter();
                param.ParameterName = pn.TrimStart(':');
                param.Value = string.IsNullOrEmpty(v) ? DBNull.Value : v;
                cmd.Parameters.Add(param);
                args.Add(info.Provider switch
                {
                    DbProvider.SqlServer => $"{p.Name} = {pn}",
                    DbProvider.PostgreSql => $"{pn}::{p.Type}",
                    _ => pn,
                });
            }
            var argList = string.Join(", ", args);
            cmd.CommandText = info.Provider switch
            {
                DbProvider.SqlServer => isProc ? $"EXEC {name} {argList}" : details.Returns is null ? $"SELECT * FROM {name}({string.Join(", ", args.Select(a => a[(a.IndexOf('=') + 1)..].Trim()))})" : $"SELECT {name}({string.Join(", ", args.Select(a => a[(a.IndexOf('=') + 1)..].Trim()))}) AS result",
                DbProvider.PostgreSql => isProc ? $"CALL {name}({argList})" : $"SELECT * FROM {name}({argList})",
                DbProvider.MySql => isProc ? $"CALL {name}({argList})" : $"SELECT {name}({argList}) AS result",
                DbProvider.Oracle => isProc ? $"BEGIN {name}({argList}); END;" : $"SELECT {name}({argList}) AS RESULT FROM DUAL",
                _ => throw new StudioException("SQLite has no procedures or functions."),
            };
            cmd.CommandTimeout = 600;
            var sw = Stopwatch.StartNew();
            var results = new List<SqlResultSet>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                do
                {
                    if (r.FieldCount == 0) continue;
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
                if (results.Count == 0) results.Add(new SqlResultSet([], [], false, r.RecordsAffected, $"{req.Name} ran without returning rows."));
            }
            return new SqlResult(results, sw.ElapsedMilliseconds, false);
        }
    });

    // ------------------------------------------------------------------ whole-database script

    /// <summary>A script that recreates the database: tables, data (optional), relations, indexes, views and routines.</summary>
    public Task<(string Content, string FileName)> ScriptDatabaseAsync(ScriptDbRequest req, CancellationToken ct) => Guard(async () =>
    {
        var (conn, info) = await OpenAsync(req.Ref, ct);
        await using (conn)
        {
            var s = await SchemaReader.ReadAsync(conn, info, ct);
            var d = SqlDialect.For(info.Provider);
            var go = info.Provider == DbProvider.SqlServer ? "\nGO\n\n" : info.Provider == DbProvider.Oracle ? ";\n/\n\n" : ";\n\n";
            var end = info.Provider == DbProvider.SqlServer ? "\nGO\n\n" : ";\n\n";
            var sb = new StringBuilder();
            sb.Append($"-- {s.Database} ({s.Provider} {s.Version}), scripted by QosriStudio on {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
            foreach (var sch in s.Schemas.Where(x => s.Capabilities.Schemas && x is not ("dbo" or "public")))
                sb.Append($"CREATE SCHEMA {d.Quote(sch)}{end}");
            foreach (var t in s.Tables)
            {
                var defs = t.Columns.Where(c => !c.Computed).Select(c => new DbsColumnDef(c.Name, c.Type, c.Nullable, c.Default, c.Identity, c.PrimaryKey)).ToList();
                sb.Append(d.Ddl(new DdlChange("createTable", t.Schema, t.Name, Columns: defs), null)[0]).Append(end);
            }
            if (req.WithData)
            {
                foreach (var t in s.Tables)
                {
                    var cols = t.Columns.Where(c => !c.Computed).ToList();
                    if (cols.Count == 0) continue;
                    var names = string.Join(", ", cols.Select(c => d.Quote(c.Name)));
                    var target = d.Table(t.Schema, t.Name);
                    var order = t.PrimaryKey.Count > 0 ? string.Join(", ", t.PrimaryKey.Select(d.Quote)) : info.Provider == DbProvider.SqlServer ? "(SELECT NULL)" : "1";
                    var any = false;
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = d.Page($"SELECT {names} FROM {target}", order, 0, 100_000);
                    cmd.CommandTimeout = 600;
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        if (!any && info.Provider == DbProvider.SqlServer && cols.Any(c => c.Identity)) sb.Append($"SET IDENTITY_INSERT {target} ON{end}");
                        any = true;
                        var values = Enumerable.Range(0, r.FieldCount).Select(i => Literal(Text(r, i), cols[i]));
                        sb.Append($"INSERT INTO {target} ({names}) VALUES ({string.Join(", ", values)})").Append(info.Provider == DbProvider.SqlServer ? ";\n" : ";\n");
                    }
                    if (any && info.Provider == DbProvider.SqlServer && cols.Any(c => c.Identity)) sb.Append($"SET IDENTITY_INSERT {target} OFF{end}");
                    else if (any) sb.Append('\n');
                }
            }
            foreach (var fk in s.ForeignKeys)
            {
                if (info.Provider == DbProvider.Sqlite) break;
                sb.Append(d.Ddl(new DdlChange("addForeignKey", fk.Schema, fk.Table, ConstraintName: fk.Name, KeyColumns: fk.Columns, RefSchema: fk.RefSchema, RefTable: fk.RefTable, RefColumns: fk.RefColumns, OnDelete: fk.OnDelete), null)[0]).Append(end);
            }
            foreach (var ix in s.Indexes)
                sb.Append(d.Ddl(new DdlChange("addIndex", ix.Schema, ix.Table, ConstraintName: ix.Name, KeyColumns: ix.Columns, Unique: ix.Unique), null)[0]).Append(end);
            foreach (var v in s.Views.Where(v => !string.IsNullOrWhiteSpace(v.Definition)))
            {
                var def = v.Definition!.Trim().TrimEnd(';');
                sb.Append(def.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ? def : $"CREATE VIEW {d.Table(v.Schema, v.Name)} AS\n{def}").Append(end);
            }
            foreach (var rt in s.Routines.Where(x => x.Kind != "Package"))
            {
                var def = await SchemaReader.RoutineDefinitionAsync(conn, info, rt.Schema, rt.Name, rt.Kind, ct);
                if (def.StartsWith('(')) continue;
                def = ExtrasReader.Editable(info.Provider, def, rt.Kind).Trim();
                if (info.Provider == DbProvider.MySql) sb.Append("DELIMITER $$\n").Append(def.TrimEnd(';')).Append("$$\nDELIMITER ;\n\n");
                else sb.Append(def.TrimEnd(';').TrimEnd()).Append(info.Provider == DbProvider.Oracle ? (def.TrimEnd().EndsWith(";") ? "\n/\n\n" : ";\n/\n\n") : go);
            }
            return (sb.ToString(), $"{(string.IsNullOrEmpty(s.Database) ? "database" : s.Database)}{(req.WithData ? "" : "-schema")}.sql");
        }
    });

    // ------------------------------------------------------------------ backups

    public Task<string> BackupAsync(BackupRequest req, CancellationToken ct) => Guard(async () =>
    {
        var info = Resolve(req.Ref);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        switch (info.Provider)
        {
            case DbProvider.SqlServer:
            {
                var (conn, _) = await OpenAsync(req.Ref, ct);
                await using (conn)
                {
                    var folder = req.Folder?.Trim();
                    if (string.IsNullOrEmpty(folder))
                    {
                        // The server writes the file itself, so its own backup folder always works
                        await using var q = conn.CreateCommand();
                        q.CommandText = "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'))";
                        folder = (await q.ExecuteScalarAsync(ct)) as string;
                        if (string.IsNullOrEmpty(folder)) throw new StudioException("Enter a folder on the database server to save the backup in.");
                    }
                    var file = Path.Combine(folder, $"{conn.Database}-{stamp}.bak");
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"BACKUP DATABASE [{conn.Database.Replace("]", "]]")}] TO DISK = @f WITH INIT, COPY_ONLY";
                    cmd.CommandTimeout = 3600;
                    var p = cmd.CreateParameter(); p.ParameterName = "@f"; p.Value = file; cmd.Parameters.Add(p);
                    await cmd.ExecuteNonQueryAsync(ct);
                    return $"Backed up to {file} (on the database server).";
                }
            }
            case DbProvider.Sqlite:
            {
                var folder = string.IsNullOrWhiteSpace(req.Folder) ? Path.GetDirectoryName(info.Server)! : req.Folder.Trim();
                if (!Directory.Exists(folder)) throw new StudioException("That folder doesn't exist.");
                var file = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(info.Server)}-{stamp}{Path.GetExtension(info.Server)}");
                var (conn, _) = await OpenAsync(req.Ref, ct);
                await using (conn)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "VACUUM INTO @f";
                    var p = cmd.CreateParameter(); p.ParameterName = "@f"; p.Value = file; cmd.Parameters.Add(p);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                return $"Backed up to {file}.";
            }
            case DbProvider.PostgreSql or DbProvider.MySql:
            {
                var folder = req.Folder?.Trim();
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new StudioException("Choose an existing folder to save the backup in.");
                var pg = info.Provider == DbProvider.PostgreSql;
                var tool = FindTool(pg ? "pg_dump" : "mysqldump", pg ? "PostgreSQL" : "MySQL")
                           ?? throw new StudioException($"{(pg ? "pg_dump" : "mysqldump")} wasn't found on this computer. Install the {(pg ? "PostgreSQL" : "MySQL")} client tools, or use “Save as SQL script” instead.");
                var file = Path.Combine(folder, $"{info.Database}-{stamp}.{(pg ? "dump" : "sql")}");
                var psi = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                var host = info.Server.Trim();
                if (pg)
                {
                    foreach (var a in new[] { "-h", host, "-p", (info.Port ?? 5432).ToString(), "-U", info.Username ?? "postgres", "-F", "c", "-f", file, info.Database ?? "" }) psi.ArgumentList.Add(a);
                    psi.Environment["PGPASSWORD"] = info.Password ?? "";
                }
                else
                {
                    foreach (var a in new[] { "--host", host, "--port", (info.Port ?? 3306).ToString(), "--user", info.Username ?? "root", "--routines", "--triggers", "--single-transaction", $"--result-file={file}", info.Database ?? "" }) psi.ArgumentList.Add(a);
                    psi.Environment["MYSQL_PWD"] = info.Password ?? "";
                }
                using var proc = Process.Start(psi)!;
                var err = proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0) throw new StudioException("The backup failed: " + (await err).Trim());
                return $"Backed up to {file}.";
            }
            default:
                throw new StudioException("Oracle backups are made with RMAN or Data Pump by your DBA. Use “Save as SQL script” to copy this schema.");
        }
    });

    private static string? FindTool(string exe, string vendorFolder)
    {
        var name = OperatingSystem.IsWindows() ? exe + ".exe" : exe;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var vendor = Path.Combine(root, vendorFolder);
            if (!Directory.Exists(vendor)) continue;
            var found = Directory.EnumerateFiles(vendor, name, SearchOption.AllDirectories).OrderByDescending(f => f).FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>Creates an empty SQLite database file.</summary>
    public static string CreateSqliteFile(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (!full.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
            full += ".db";
        if (File.Exists(full)) throw new StudioException("That file already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new StudioException("That folder doesn't exist.");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = full, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 1";
            cmd.ExecuteNonQuery();
        }
        return full;
    }
}
