using Stackwright.Server.Models;

namespace Stackwright.Server.Services.Db;

public record DbsColumn(string Name, string Type, bool Nullable, string? Default, bool Identity, bool PrimaryKey, bool Computed, int Position, int? MaxLength);
public record DbsTable(string Schema, string Name, long? Rows, List<DbsColumn> Columns, List<string> PrimaryKey, string? PkName);
public record DbsForeignKey(string Name, string Schema, string Table, List<string> Columns, string RefSchema, string RefTable, List<string> RefColumns, string OnDelete, string OnUpdate);
public record DbsIndex(string Name, string Schema, string Table, bool Unique, List<string> Columns);
public record DbsView(string Schema, string Name, string? Definition);
public record DbsRoutine(string Schema, string Name, string Kind);
public record DbsCapabilities(bool Schemas, bool AlterColumn, bool AddForeignKey, bool Routines, bool RenameColumn);
public record DbSchema(string Provider, string Database, string Version, List<DbsTable> Tables, List<DbsForeignKey> ForeignKeys, List<DbsIndex> Indexes,
    List<DbsView> Views, List<DbsRoutine> Routines, List<string> Types, List<string> Schemas, DbsCapabilities Capabilities);

public record SavedConnection(string Id, string Name, DbConnectionInfo Info, bool HasPassword, DateTime Saved);
public record SaveConnectionRequest(string? Id, string Name, DbConnectionInfo Info);

/// <summary>Points at a database: a saved connection (its password stays on the server) or connection details.</summary>
public record DbRef(string? ConnectionId, DbConnectionInfo? Connection, string? Database);

public record RowsRequest(DbRef Ref, string Schema, string Table, int Offset, int Limit, string? OrderBy, bool Desc, string? Search, Dictionary<string, string?>? Filters);
public record RowsResult(List<string> Columns, List<List<string?>> Rows, long Total, List<string> KeyColumns, bool ReadOnly);
public record RowChange(DbRef Ref, string Schema, string Table, Dictionary<string, string?>? Keys, Dictionary<string, string?>? Values, List<Dictionary<string, string?>>? KeyList);
public record ImportRequest(DbRef Ref, string Schema, string Table, List<Dictionary<string, string?>> Rows);

public record DbsColumnDef(string Name, string Type, bool Nullable, string? Default, bool Identity, bool PrimaryKey);
public record DdlChange(
    string Kind,
    string? Schema = null,
    string? Table = null,
    DbsColumnDef? Column = null,
    List<DbsColumnDef>? Columns = null,
    string? OldName = null,
    string? NewName = null,
    string? ConstraintName = null,
    List<string>? KeyColumns = null,
    string? RefSchema = null,
    string? RefTable = null,
    List<string>? RefColumns = null,
    string? OnDelete = null,
    bool Unique = false,
    string? Sql = null);
public record DdlRequest(DbRef Ref, DdlChange Change);

public record SqlRequest(DbRef Ref, string Sql, bool DryRun, bool Confirmed);
public record SqlResultSet(List<string> Columns, List<List<string?>> Rows, bool Truncated, int? Affected, string? Message);
public record SqlResult(List<SqlResultSet> Results, long DurationMs, bool RolledBack, string? NeedsConfirm = null);
public record ExportRequest(DbRef Ref, string Schema, string Table, string Format, string? Search);
public record DdlPreview(List<string> Statements, string? Warning);
public record RoutineRequest(DbRef Ref, string Schema, string Name, string Kind);
