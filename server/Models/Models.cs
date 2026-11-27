namespace QosriStudio.Server.Models;

// ---------- File system ----------
public record DriveEntry(string Name, string Path, string? Label);
public record FolderEntry(string Name, string Path);
public record ProjectFileEntry(string Name, string Path, string? Framework, bool IsWeb, string Kind = "dotnet");
public record SolutionEntry(string Name, string Path);
public record FileEntry(string Name, string Path, long Size);
public record BrowseResult(string? Path, string? Parent, List<FolderEntry> Folders, List<ProjectFileEntry> Projects, List<SolutionEntry> Solutions, List<FileEntry>? Files = null);

// ---------- Projects ----------
public record RecentProject(string Name, string Path, DateTime LastOpened, int EndpointCount);

public enum EndpointStyle { Controller, MinimalApi }
public enum ParamSource { Route, Query, Body, Header, Service }

public record EndpointParam(string Name, string Type, ParamSource Source, bool Optional);

public record EndpointInfo(
    string Id,
    string Project,
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
    int Line,
    string? Calls = null,
    string? BodySample = null);

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

public record ProjectRefInfo(string Name, string Path, string? Framework, string Role, bool IsWeb);
public record ServiceParam(string Name, string Type, string Friendly, bool Injected);
public record ServiceOperation(string Name, List<ServiceParam> Parameters, string ReturnType, string Returns, bool IsAsync, bool Exposed);
public record ServiceInfo(string Interface, string? Implementation, string Project, int Methods, string Namespace, List<ServiceOperation> Operations);
public record DataAccessInfo(List<string> SqlProviders, List<string> ConnectionStrings);

public record SolutionProjectInfo(string Name, string Path, string? Framework, string Role, bool IsWeb, List<string> References, int? EndpointCount, int FileCount);
public record SolutionDetails(string Name, string Path, List<SolutionProjectInfo> Projects);

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
    List<string> Warnings,
    string? SolutionPath,
    List<ProjectRefInfo> ReferencedProjects,
    List<string> BinaryReferences,
    List<ServiceInfo> Services,
    DataAccessInfo DataAccess);

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
    public const string ServiceCall = "serviceCall";
    public const string Sql = "sql";
    public const string Template = "template";

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
    List<EndpointParam>? Parameters,
    // Call a service operation / copy an existing endpoint
    string? ServiceInterface = null,
    string? ServiceOperation = null,
    bool NewOperation = false,
    string? TemplateEndpointId = null,
    // Run SQL
    string? Sql = null,
    string? SqlKind = null,          // "query" or "procedure"
    string? SqlProvider = null,
    string? ConnectionName = null,
    string? ConnectionString = null);  // only when adding a new connection string

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
public enum DbProvider { SqlServer, MySql, PostgreSql, Oracle, Sqlite }

public record DbConnectionInfo(
    DbProvider Provider,
    string Server,
    int? Port,
    bool IntegratedSecurity,
    string? Username,
    string? Password,
    string? Database,
    string? ServiceName = null);

public record DbColumn(string Name, string SqlType, string ClrType, bool Nullable, bool IsPrimaryKey, bool IsIdentity, bool IsComputed, int? MaxLength, bool Supported);
public record DbTable(string Schema, string Name, List<DbColumn> Columns, bool IsView, long? RowCount);

public record GenerateProjectRequest(
    DbConnectionInfo Connection,
    string? TargetFramework,
    string ProjectName,
    string Location,
    EndpointStyle Style,
    List<TableSelection> Tables,
    bool IncludeApiDocs,
    bool CleanArchitecture = true,
    bool Jwt = true);

public record TableSelection(string Schema, string Name, List<string> Operations);

public record TargetOption(string Tfm, int Major, string Label, string Support, bool CanBuild, bool CanRun, bool Recommended);

public record GenerateProjectResult(string ProjectPath, List<string> CreatedFiles, BuildResult? Build, string? SolutionPath = null, string? AdminUser = null, string? AdminPassword = null);

public class StudioException(string message) : Exception(message);
