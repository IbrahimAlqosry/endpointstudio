namespace EndpointStudio.Server.Models;

// ---------- File system ----------
public record DriveEntry(string Name, string Path, string? Label);
public record FolderEntry(string Name, string Path);
public record ProjectFileEntry(string Name, string Path, string? Framework, bool IsWeb);
public record SolutionEntry(string Name, string Path);
public record BrowseResult(string? Path, string? Parent, List<FolderEntry> Folders, List<ProjectFileEntry> Projects, List<SolutionEntry> Solutions);

// ---------- Projects ----------
public record RecentProject(string Name, string Path, DateTime LastOpened, int EndpointCount);

public enum EndpointStyle { Controller, MinimalApi }
public enum ParamSource { Route, Query, Body, Header, Service }

public record EndpointParam(string Name, string Type, ParamSource Source, bool Optional);

public record EndpointInfo(
    string Id,
    EndpointStyle Style,
    string HttpMethod,
    string Route,
    string RelativeRoute,
    string GroupKey,
    string Name,
    string? Summary,
    List<EndpointParam> Parameters,
    string Returns,
    bool RequiresAuth,
    string File,
    int Line);

public record EndpointGroup(string Key, string Name, EndpointStyle Style, string BaseRoute, string File, int EndpointCount);

public record EntityProperty(string Name, string Type, bool IsKey, bool Nullable);

public record EntityInfo(
    string Name,
    string Namespace,
    string DbSetName,
    string ContextName,
    string ContextNamespace,
    string? KeyName,
    string? KeyType,
    List<EntityProperty> Properties);

public record HistoryEntry(string Id, DateTime Time, string Description);

public record ProjectCapabilities(
    string Platform,
    string FrameworkLabel,
    bool SupportsMinimalApis,
    bool SupportsHeaderInputs,
    bool CanBuild,
    bool CanRun,
    string? BuildNote,
    string? RunNote,
    string? SupportNote);

public record ProjectDetails(
    string Name,
    string Path,
    string Directory,
    string TargetFramework,
    ProjectCapabilities Capabilities,
    string RootNamespace,
    EndpointStyle PreferredStyle,
    List<EndpointInfo> Endpoints,
    List<EndpointGroup> Groups,
    List<EntityInfo> Entities,
    List<string> Packages,
    List<HistoryEntry> History,
    List<string> Warnings);

// ---------- Endpoint editing ----------
public static class Behaviors
{
    public const string Message = "message";
    public const string Echo = "echo";
    public const string NotImplemented = "notImplemented";
    public const string NoContent = "noContent";
    public const string EntityList = "entityList";
    public const string EntityGet = "entityGet";
    public const string EntityCreate = "entityCreate";
    public const string EntityUpdate = "entityUpdate";
    public const string EntityDelete = "entityDelete";
    public const string EntityFilter = "entityFilter";

    public static bool IsEntity(string b) => b.StartsWith("entity", StringComparison.Ordinal);
}

public record NewGroupRequest(string Name, EndpointStyle Style, string BaseRoute);

public record AddEndpointRequest(
    string? GroupKey,
    NewGroupRequest? NewGroup,
    string HttpMethod,
    string Route,
    string Name,
    string? Summary,
    bool RequiresAuth,
    string Behavior,
    string? Message,
    string? Entity,
    string? FilterProperty,
    List<EndpointParam>? Parameters);

public record UpdateEndpointRequest(
    string HttpMethod,
    string Route,
    string Name,
    string? Summary,
    bool RequiresAuth);

// ---------- Build / run ----------
public record BuildIssue(string? File, int? Line, string Code, string Message);
public record BuildResult(bool Success, int ErrorCount, int WarningCount, List<BuildIssue> Errors, long DurationMs);

public record RunStatus(bool Running, string? Url, string State, List<string> Log);

public record TryRequest(string Method, string Path, Dictionary<string, string>? Headers, string? Body);
public record TryResponse(int Status, string StatusText, Dictionary<string, string> Headers, string Body, long DurationMs, string? ContentType);

// ---------- Database ----------
public enum DbProvider { SqlServer, MySql }

public record DbConnectionInfo(
    DbProvider Provider,
    string Server,
    int? Port,
    bool IntegratedSecurity,
    string? Username,
    string? Password,
    string? Database);

public record DbColumn(string Name, string SqlType, string ClrType, bool Nullable, bool IsPrimaryKey, bool IsIdentity, bool IsComputed, int? MaxLength, bool Supported);
public record DbTable(string Schema, string Name, List<DbColumn> Columns, bool IsView, long? RowCount);

public record GenerateProjectRequest(
    DbConnectionInfo Connection,
    string? TargetFramework,
    string ProjectName,
    string Location,
    EndpointStyle Style,
    List<TableSelection> Tables,
    bool IncludeApiDocs);

public record TableSelection(string Schema, string Name, List<string> Operations);

public record TargetOption(string Tfm, int Major, string Label, string Support, bool CanBuild, bool CanRun, bool Recommended);

public record GenerateProjectResult(string ProjectPath, List<string> CreatedFiles, BuildResult? Build);

public class StudioException(string message) : Exception(message);
