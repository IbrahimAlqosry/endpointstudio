export type EndpointStyle = 'Controller' | 'MinimalApi'
export type ParamSource = 'Route' | 'Query' | 'Body' | 'Header' | 'Service'
export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface FolderEntry { name: string; path: string }
export interface ProjectFileEntry { name: string; path: string; framework: string | null; isWeb: boolean }
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
export interface ServiceInfo { interface: string; implementation: string | null; project: string; methods: number }
export interface SolutionProjectInfo { name: string; path: string; framework: string | null; role: string; isWeb: boolean; references: string[]; endpointCount: number | null; fileCount: number }
export interface SolutionDetails { name: string; path: string; projects: SolutionProjectInfo[] }

export interface HealthFinding { severity: 'high' | 'medium' | 'low' | 'info'; category: string; title: string; detail: string; items: string[]; endpointIds?: string[] | null }
export interface HealthReport { score: number; grade: string; findings: HealthFinding[]; checks: number }
export interface UpgradeStep { project: string; kind: string; title: string; detail: string }
export interface UpgradePlan { from: string; to: string; targetMajor: number; steps: UpgradeStep[]; warnings: string[]; canApply: boolean; projects: string[] }
export interface UpgradeRequest { targetMajor: number; includeReferences: boolean }

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
