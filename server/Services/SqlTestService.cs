using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stackwright.Server.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Stackwright.Server.Services;

public record SqlTestRequest(string Provider, string? ConnectionName, string? ConnectionString, string Sql, Dictionary<string, string?>? Values);
public record SqlTestResult(List<string> Columns, List<List<string?>> Rows, bool More, long DurationMs);

/// <summary>Runs a query the user is about to turn into an endpoint, safely: reads only, inside a transaction that is rolled back.</summary>
public static class SqlTestService
{
    public static async Task<SqlTestResult> RunAsync(ProjectModel model, SqlTestRequest req, CancellationToken ct)
    {
        var sql = (req.Sql ?? "").Trim();
        if (!Regex.IsMatch(sql, @"^(select|with)\b", RegexOptions.IgnoreCase))
            throw new StudioException("Only queries that read data (SELECT) can be tested here. Commands that change data run only from the real endpoint.");

        var cs = !string.IsNullOrWhiteSpace(req.ConnectionString) ? req.ConnectionString!.Trim() : FindConnectionString(model, req.ConnectionName)
                 ?? throw new StudioException($"The connection string “{req.ConnectionName}” wasn't found in the project's settings.");

        DbConnection conn = req.Provider switch
        {
            "SqlServer" => new SqlConnection(cs),
            "PostgreSql" => new NpgsqlConnection(cs),
            "MySql" => new MySqlConnection(cs),
            "Oracle" => new OracleConnection(cs),
            "Sqlite" => new SqliteConnection(cs),
            _ => throw new StudioException("Choose the kind of database first."),
        };
        await using (conn)
        {
            try { await conn.OpenAsync(ct); }
            catch (Exception ex) { throw new StudioException("Couldn't connect with this connection string: " + ex.Message); }
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.CommandTimeout = 20;
            if (cmd is OracleCommand oc) oc.BindByName = true;
            var prefix = req.Provider == "Oracle" ? "" : "@";
            foreach (var name in Codegen.EndpointWriter.SqlParameterNames(sql))
            {
                var p = cmd.CreateParameter();
                p.ParameterName = prefix + name;
                var raw = req.Values?.FirstOrDefault(v => v.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                p.Value = string.IsNullOrEmpty(raw) ? DBNull.Value : raw;
                cmd.Parameters.Add(p);
            }

            var sw = Stopwatch.StartNew();
            var columns = new List<string>();
            var rows = new List<List<string?>>();
            var more = false;
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count == 20) { more = true; break; }
                    var row = new List<string?>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        row.Add(reader.IsDBNull(i) ? null : Format(reader.GetValue(i)));
                    rows.Add(row);
                }
            }
            catch (DbException ex) { throw new StudioException("The database didn't accept the query: " + ex.Message); }
            finally { try { await tx.RollbackAsync(ct); } catch { /* nothing to undo */ } }
            return new SqlTestResult(columns, rows, more, sw.ElapsedMilliseconds);
        }
    }

    private static string Format(object v) => v switch
    {
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
        byte[] b => $"({b.Length} bytes)",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    public static string? FindConnectionString(ProjectModel model, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var cfg in new[] { "appsettings.Development.json", "appsettings.json" })
        {
            var path = Path.Combine(model.Directory, cfg);
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("ConnectionStrings", out var s) && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch { /* ignore */ }
        }
        var web = Path.Combine(model.Directory, "Web.config");
        if (File.Exists(web))
        {
            var add = System.Xml.Linq.XDocument.Load(web).Descendants("add").FirstOrDefault(a => (string?)a.Attribute("name") == name);
            if (add is not null) return (string?)add.Attribute("connectionString");
        }
        return null;
    }
}
