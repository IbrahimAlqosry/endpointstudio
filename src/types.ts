export type EndpointStyle = 'Controller' | 'MinimalApi'
export type ParamSource = 'Route' | 'Query' | 'Body' | 'Header' | 'Service'
export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface FolderEntry { name: string; path: string }
export interface ProjectFileEntry { name: string; path: string; framework: string | null; isWeb: boolean; kind?: 'dotnet' | 'frontend' }
export interface SolutionEntry { name: string; path: string }
export interface FileEntry { name: string; path: string; size: number }
export interface BrowseResult { path: string | null; parent: string | null; folders: FolderEntry[]; projects: ProjectFileEntry[]; solutions: SolutionEntry[]; files?: FileEntry[] | null }

export interface RecentProject { name: string; path: string; lastOpened: string; endpointCount: number }

export interface EndpointParam { name: string; type: string; source: ParamSource; optional: boolean }

export interface EndpointInfo {
  id: string
  project: string
  style: EndpointStyle
  httpMethod: string
  route: string
  relativeRoute: string
  groupKey: string
  name: string
  summary: string | null
  parameters: EndpointParam[]
  returns: string
  requiresAuth: boolean
  file: string
  line: number
  calls?: string | null
  bodySample?: string | null
}

export interface EndpointGroup { key: string; name: string; style: EndpointStyle; baseRoute: string; file: string; endpointCount: number }

export interface EntityProperty { name: string; type: string; isKey: boolean; nullable: boolean }
export interface EntityInfo {
  name: string
  namespace: string
  dbSetName: string
  contextName: string
  contextNamespace: string
  keyName: string | null
  keyType: string | null
  properties: EntityProperty[]
}

export interface ProjectRefInfo { name: string; path: string; framework: string | null; role: string; isWeb: boolean }
export interface ServiceParam { name: string; type: string; friendly: string; injected: boolean }
export interface ServiceOperation { name: string; parameters: ServiceParam[]; returnType: string; returns: string; isAsync: boolean; exposed: boolean }
export interface ServiceInfo { interface: string; implementation: string | null; project: string; methods: number; namespace: string; operations: ServiceOperation[] }
export interface DataAccessInfo { sqlProviders: string[]; connectionStrings: string[] }
export interface SqlTestRequest { provider: string; connectionName: string | null; connectionString: string | null; sql: string; values: Record<string, string | null> }
export interface SqlTestResult { columns: string[]; rows: (string | null)[][]; more: boolean; durationMs: number }
export interface SolutionProjectInfo { name: string; path: string; framework: string | null; role: string; isWeb: boolean; references: string[]; endpointCount: number | null; fileCount: number }
export interface SolutionDetails { name: string; path: string; projects: SolutionProjectInfo[] }

export interface HealthFinding { severity: 'high' | 'medium' | 'low' | 'info'; category: string; title: string; detail: string; items: string[]; endpointIds?: string[] | null }
export interface HealthReport { score: number; grade: string; findings: HealthFinding[]; checks: number }
export interface UpgradeStep { project: string; kind: string; title: string; detail: string }
export interface UpgradePlan { from: string; to: string; targetMajor: number; steps: UpgradeStep[]; warnings: string[]; canApply: boolean; projects: string[] }
export interface UpgradeRequest { targetMajor: number; includeReferences: boolean }

export interface SettingEntry { key: string; value: string | null; kind: 'string' | 'number' | 'bool' | 'null' | 'section' | 'array' | 'error'; secret: boolean }
export interface SettingsFile { name: string; path: string; format: 'json' | 'xml' | 'ts' | 'env'; environment: string | null; hasComments: boolean; entries: SettingEntry[] }
export interface LaunchProfile { name: string; urls: string | null; environment: string | null }
export interface SettingsInfo { files: SettingsFile[]; environments: string[]; launchProfiles: LaunchProfile[] }
export interface SettingChange { key: string; value: string | null; kind: string; remove: boolean }

export type DeployTarget = 'folder' | 'zip' | 'docker' | 'dockerRemote' | 'ssh' | 'webdeploy' | 'azure' | 'ftp'
export interface DeployRequest {
  target: DeployTarget; profileName?: string | null; configuration?: string; runtime?: string | null; selfContained?: boolean; singleFile?: boolean
  environment?: string | null; outputPath?: string | null; imageName?: string | null; imageTag?: string | null; runLocally?: boolean; hostPort?: number | null
  containerName?: string | null; push?: boolean; registry?: string | null; registryUser?: string | null; registryPassword?: string | null
  host?: string | null; port?: number | null; user?: string | null; keyFile?: string | null; remotePath?: string | null; restartCommand?: string | null
  siteName?: string | null; password?: string | null; resourceGroup?: string | null; appName?: string | null; ftps?: boolean; iisRewrite?: boolean
}
export interface DeployProfile { id: string; name: string; request: DeployRequest; saved: string }
export interface DeployTools { docker: boolean; dockerRunning: boolean; dockerVersion: string | null; ssh: boolean; msDeploy: boolean; azureCli: boolean; visualStudio: boolean }
export interface JobStatus { id: string; kind: string; title: string; state: 'running' | 'succeeded' | 'failed' | 'cancelled'; log: string[]; result: string | null; resultUrl: string | null; started: string; finished: string | null }

export interface AssemblyEntry { name: string; path: string; version: string | null; framework: string | null; isOwnCode: boolean; isMainApp: boolean; controllers: number; types: number; size: number; obfuscated: boolean }
export interface InspectResult { folder: string; assemblies: AssemblyEntry[]; mainApp: string | null }

export interface FrontendRoute { path: string; component: string | null; kind: string; guards: string[]; file: string; line: number }
export interface FrontendComponent { name: string; kind: string; selector: string | null; file: string; line: number }
export interface FrontendApiCall { id: string; method: string; url: string; path: string; base: string | null; caller: string | null; file: string; line: number; match: 'matched' | 'missing' | 'method' | 'unknown' | 'asset'; endpointId: string | null; endpointRoute: string | null; endpointMethod: string | null }
export interface EndpointBrief { id: string; method: string; route: string; name: string }
export interface FrontendDetails {
  kind: 'frontend'; name: string; path: string; directory: string; framework: string; frameworkVersion: string | null; frameworkMajor: number
  buildTool: string | null; packageManager: string; installed: boolean; typeScript: boolean; scripts: Record<string, string>; outputDir: string | null; devScript: string | null
  routes: FrontendRoute[]; components: FrontendComponent[]; apiCalls: FrontendApiCall[]; warnings: string[]; sourceFiles: number
  linkedBackend: string | null; linkedBackendName: string | null; unusedEndpoints: EndpointBrief[]; suggestedBackends: ProjectFileEntry[]; supportNote: string | null; history: HistoryEntry[]
}

export interface HistoryEntry { id: string; time: string; description: string }

export interface ProjectCapabilities {
  platform: 'NetCore' | 'NetFramework'
  frameworkLabel: string
  supportsMinimalApis: boolean
  supportsHeaderInputs: boolean
  canBuild: boolean
  canRun: boolean
  buildNote: string | null
  runNote: string | null
  supportNote: string | null
}

export interface ProjectDetails {
  name: string
  path: string
  directory: string
  targetFramework: string
  capabilities: ProjectCapabilities
  rootNamespace: string
  preferredStyle: EndpointStyle
  endpoints: EndpointInfo[]
  groups: EndpointGroup[]
  entities: EntityInfo[]
  packages: string[]
  history: HistoryEntry[]
  warnings: string[]
  solutionPath: string | null
  referencedProjects: ProjectRefInfo[]
  binaryReferences: string[]
  services: ServiceInfo[]
  dataAccess: DataAccessInfo
}

export interface AddEndpointRequest {
  groupKey: string | null
  newGroup: { name: string; style: EndpointStyle; baseRoute: string } | null
  httpMethod: string
  route: string
  name: string
  summary: string | null
  requiresAuth: boolean
  behavior: string
  message: string | null
  entity: string | null
  filterProperty: string | null
  parameters: EndpointParam[] | null
  serviceInterface?: string | null
  serviceOperation?: string | null
  newOperation?: boolean
  templateEndpointId?: string | null
  sql?: string | null
  sqlKind?: 'query' | 'procedure' | null
  sqlProvider?: string | null
  connectionName?: string | null
  connectionString?: string | null
}

export interface UpdateEndpointRequest { httpMethod: string; route: string; name: string; summary: string | null; requiresAuth: boolean }

export interface ChangeResult { message: string; project: ProjectDetails }

export interface BuildIssue { file: string | null; line: number | null; code: string; message: string }
export interface BuildResult { success: boolean; errorCount: number; warningCount: number; errors: BuildIssue[]; durationMs: number }

export interface RunStatus { running: boolean; url: string | null; state: 'starting' | 'running' | 'stopped' | 'failed' | 'stopping'; log: string[] }

export interface TryRequest { method: string; path: string; headers: Record<string, string>; body: string | null }
export interface TryResponse { status: number; statusText: string; headers: Record<string, string>; body: string; durationMs: number; contentType: string | null }

export type DbProvider = 'SqlServer' | 'MySql' | 'PostgreSql' | 'Oracle' | 'Sqlite'
export interface DbConnectionInfo {
  provider: DbProvider
  server: string
  port: number | null
  integratedSecurity: boolean
  username: string | null
  password: string | null
  database: string | null
  serviceName?: string | null
}
export interface DbColumn { name: string; sqlType: string; clrType: string; nullable: boolean; isPrimaryKey: boolean; isIdentity: boolean; isComputed: boolean; maxLength: number | null; supported: boolean }
export interface DbTable { schema: string; name: string; columns: DbColumn[]; isView: boolean; rowCount: number | null }

export interface TargetOption { tfm: string; major: number; label: string; support: string; canBuild: boolean; canRun: boolean; recommended: boolean }

export interface SystemInfo {
  dotnetVersion: string
  sdks: number[]
  runtimes: number[]
  hasVisualStudio: boolean
  hasIisExpress: boolean
  targets: TargetOption[]
  defaultLocation: string
}

export interface GenerateProjectRequest {
  connection: DbConnectionInfo
  targetFramework: string
  projectName: string
  location: string
  style: EndpointStyle
  tables: { schema: string; name: string; operations: string[] }[]
  includeApiDocs: boolean
}
export interface GenerateProjectResult { projectPath: string; createdFiles: string[]; build: BuildResult | null }

// ---------- Database Studio ----------
export interface DbsColumn { name: string; type: string; nullable: boolean; default: string | null; identity: boolean; primaryKey: boolean; computed: boolean; position: number; maxLength: number | null }
export interface DbsTable { schema: string; name: string; rows: number | null; columns: DbsColumn[]; primaryKey: string[]; pkName: string | null }
export interface DbsForeignKey { name: string; schema: string; table: string; columns: string[]; refSchema: string; refTable: string; refColumns: string[]; onDelete: string; onUpdate: string }
export interface DbsIndex { name: string; schema: string; table: string; unique: boolean; columns: string[] }
export interface DbsView { schema: string; name: string; definition: string | null }
export interface DbsRoutine { schema: string; name: string; kind: string }
export interface DbSchema {
  provider: DbProvider; database: string; version: string; tables: DbsTable[]; foreignKeys: DbsForeignKey[]; indexes: DbsIndex[]; views: DbsView[]; routines: DbsRoutine[]
  types: string[]; schemas: string[]; capabilities: { schemas: boolean; alterColumn: boolean; addForeignKey: boolean; routines: boolean; renameColumn: boolean }
}
export interface SavedConnection { id: string; name: string; info: DbConnectionInfo; hasPassword: boolean; saved: string }
export interface DbRef { connectionId?: string | null; connection?: DbConnectionInfo | null; database?: string | null }
export interface RowsResult { columns: string[]; rows: (string | null)[][]; total: number; keyColumns: string[]; readOnly: boolean }
export interface DbsColumnDef { name: string; type: string; nullable: boolean; default?: string | null; identity: boolean; primaryKey: boolean }
export interface DdlChange {
  kind: string; schema?: string | null; table?: string | null; column?: DbsColumnDef | null; columns?: DbsColumnDef[] | null; oldName?: string | null; newName?: string | null
  constraintName?: string | null; keyColumns?: string[] | null; refSchema?: string | null; refTable?: string | null; refColumns?: string[] | null; onDelete?: string | null; unique?: boolean; sql?: string | null
}
export interface SqlResultSet { columns: string[]; rows: (string | null)[][]; truncated: boolean; affected: number | null; message: string | null }
export interface SqlResult { results: SqlResultSet[]; durationMs: number; rolledBack: boolean; needsConfirm: string | null }
