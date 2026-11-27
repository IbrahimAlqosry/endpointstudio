using QosriStudio.Server.Models;

namespace QosriStudio.Server.Services.Db;

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
    string? Sql = null,
    List<string>? Privileges = null,
    string? Principal = null,
    string? Password = null,
    bool WithData = false,
    bool Enabled = true);
public record DdlRequest(DbRef Ref, DdlChange Change);

public record SqlRequest(DbRef Ref, string Sql, bool DryRun, bool Confirmed);
public record SqlResultSet(List<string> Columns, List<List<string?>> Rows, bool Truncated, int? Affected, string? Message);
public record SqlResult(List<SqlResultSet> Results, long DurationMs, bool RolledBack, string? NeedsConfirm = null);
public record ExportRequest(DbRef Ref, string Schema, string Table, string Format, string? Search);
public record DdlPreview(List<string> Statements, string? Warning);
public record RoutineRequest(DbRef Ref, string Schema, string Name, string Kind);

// ---------- Everything else a database holds ----------
public record DbsTrigger(string Schema, string Table, string Name, string? Timing, string? Events, bool Enabled);
public record DbsSequence(string Schema, string Name, string? Current, string? Increment);
public record DbsConstraint(string Schema, string Table, string Name, string Kind, List<string> Columns, string? Definition);
public record DbsPrincipal(string Name, string Kind, bool CanLogin);
public record DbsGrant(string Grantee, string Privilege, string? Grantor);
public record DbExtrasCaps(bool Triggers, bool Sequences, bool Users, bool Databases, bool Backup, bool CheckConstraints);
public record DbExtras(List<DbsTrigger> Triggers, List<DbsSequence> Sequences, List<DbsConstraint> Constraints, List<DbsPrincipal> Principals, List<string> Notes, DbExtrasCaps Capabilities);
public record RoutineParam(string Name, string Type, string Mode);
public record RoutineDetails(string Definition, string Editable, List<RoutineParam> Parameters, string? Returns);
public record ExecRoutineRequest(DbRef Ref, string Schema, string Name, string Kind, Dictionary<string, string?> Values);
public record TableRefRequest(DbRef Ref, string Schema, string Table);
public record TriggerRequest(DbRef Ref, string Schema, string Table, string Name);
public record BackupRequest(DbRef Ref, string Folder);
public record ScriptDbRequest(DbRef Ref, bool WithData);
