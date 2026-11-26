import type {
  AddEndpointRequest, BrowseResult, SystemInfo, BuildResult, ChangeResult, DbConnectionInfo, DbTable, GenerateProjectRequest,
  DeployProfile, DeployRequest, FrontendDetails, InspectResult, RunStatus as FeRunStatus, DeployTools, GenerateProjectResult, HealthReport, JobStatus, SettingChange, SettingsInfo, SqlTestRequest, SqlTestResult, ProjectDetails, SolutionDetails, UpgradePlan, UpgradeRequest, ProjectFileEntry, RecentProject, RunStatus, TryRequest, TryResponse, UpdateEndpointRequest,
} from './types'

export class ApiError extends Error {}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  let res: Response
  try {
    res = await fetch(url, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new ApiError('Stackwright’s helper service isn’t running. Start it with “npm run dev” and try again.')
  }
  const text = await res.text()
  let data: unknown = null
  try { data = text ? JSON.parse(text) : null } catch { /* non-json */ }
  if (!res.ok) {
    const msg = (data as { error?: string } | null)?.error
    throw new ApiError(msg ?? (res.status === 502 || res.status === 504
      ? 'Stackwright’s helper service isn’t running. Start it with “npm run dev” and try again.'
      : `Request failed (${res.status}).`))
  }
  return data as T
}

const q = (path: string) => `path=${encodeURIComponent(path)}`

export const api = {
  system: () => request<SystemInfo>('GET', '/api/system'),

  browse: (path?: string | null, ext?: string) => request<BrowseResult>('GET', `/api/fs/browse?${path ? q(path) : ''}${ext ? `&ext=${encodeURIComponent(ext)}` : ''}`),
  findProjects: (root: string) => request<ProjectFileEntry[]>('GET', `/api/fs/find-projects?root=${encodeURIComponent(root)}`),
  solutionProjects: (path: string) => request<ProjectFileEntry[]>('GET', `/api/fs/solution?${q(path)}`),
  reveal: (path: string) => request<void>('POST', '/api/fs/reveal', { path }),
  openEditor: (path: string, line?: number) => request<void>('POST', '/api/fs/open-editor', { path, line }),

  recent: () => request<RecentProject[]>('GET', '/api/projects/recent'),
  forget: (path: string) => request<void>('DELETE', `/api/projects/recent?${q(path)}`),

  project: (path: string) => request<ProjectDetails>('GET', `/api/project?${q(path)}`),
  solution: (path: string) => request<SolutionDetails>('GET', `/api/solution?${q(path)}`),
  addEndpoint: (path: string, req: AddEndpointRequest) => request<ChangeResult>('POST', `/api/project/endpoints?${q(path)}`, req),
  updateEndpoint: (path: string, id: string, req: UpdateEndpointRequest) => request<ChangeResult>('PUT', `/api/project/endpoints/${encodeURIComponent(id)}?${q(path)}`, req),
  deleteEndpoint: (path: string, id: string) => request<ChangeResult>('DELETE', `/api/project/endpoints/${encodeURIComponent(id)}?${q(path)}`),
  undo: (path: string) => request<ChangeResult>('POST', `/api/project/undo?${q(path)}`),
  build: (path: string) => request<BuildResult>('POST', `/api/project/build?${q(path)}`),
  sqlTest: (path: string, req: SqlTestRequest) => request<SqlTestResult>('POST', `/api/project/sql/test?${q(path)}`, req),
  sqlParameters: (sql: string) => request<string[]>('POST', '/api/sql/parameters', { sql }),
  settings: (path: string) => request<SettingsInfo>('GET', `/api/project/settings?${q(path)}`),
  saveSettings: (path: string, file: string, changes: SettingChange[]) => request<{ message: string; settings: SettingsInfo }>('PUT', `/api/project/settings?${q(path)}`, { file, changes }),
  addEnvironment: (path: string, name: string) => request<{ message: string; settings: SettingsInfo }>('POST', `/api/project/settings/environment?${q(path)}`, { name }),
  deployTools: () => request<DeployTools>('GET', '/api/deploy/tools'),
  deployProfiles: (path: string) => request<DeployProfile[]>('GET', `/api/project/deploy/profiles?${q(path)}`),
  deploy: (path: string, req: DeployRequest) => request<{ jobId: string }>('POST', `/api/project/deploy?${q(path)}`, req),
  buildSolution: (path: string) => request<{ jobId: string }>('POST', `/api/solution/build?${q(path)}`),
  job: (id: string) => request<JobStatus>('GET', `/api/jobs/${id}`),
  jobs: (owner: string) => request<JobStatus[]>('GET', `/api/jobs?owner=${encodeURIComponent(owner)}`),
  cancelJob: (id: string) => request<void>('POST', `/api/jobs/${id}/cancel`),
  inspectPublished: (path: string) => request<InspectResult>('POST', '/api/decompile/inspect', { path }),
  recover: (req: { folder: string; assemblies: string[]; outputFolder: string; solutionName: string | null }) => request<{ jobId: string }>('POST', '/api/decompile/recover', req),
  frontend: (path: string) => request<FrontendDetails>('GET', `/api/frontend?${q(path)}`),
  frontendLink: (path: string, backend: string | null) => request<FrontendDetails>('POST', `/api/frontend/link?${q(path)}`, { backend }),
  frontendSettings: (path: string) => request<SettingsInfo>('GET', `/api/frontend/settings?${q(path)}`),
  frontendSaveSettings: (path: string, file: string, changes: SettingChange[]) => request<{ message: string; settings: SettingsInfo }>('PUT', `/api/frontend/settings?${q(path)}`, { file, changes }),
  frontendAddEnvironment: (path: string, name: string) => request<{ message: string; settings: SettingsInfo }>('POST', `/api/frontend/settings/environment?${q(path)}`, { name }),
  frontendInstall: (path: string) => request<{ jobId: string }>('POST', `/api/frontend/install?${q(path)}`),
  frontendBuild: (path: string) => request<{ jobId: string }>('POST', `/api/frontend/build?${q(path)}`),
  frontendRunStatus: (path: string) => request<FeRunStatus>('GET', `/api/frontend/run?${q(path)}`),
  frontendRun: (path: string) => request<FeRunStatus>('POST', `/api/frontend/run?${q(path)}`),
  frontendStop: (path: string) => request<FeRunStatus>('POST', `/api/frontend/stop?${q(path)}`),
  frontendHealth: (path: string) => request<HealthReport>('GET', `/api/frontend/health?${q(path)}`),
  frontendUndo: (path: string) => request<{ message: string; project: FrontendDetails }>('POST', `/api/frontend/undo?${q(path)}`),
  health: (path: string) => request<HealthReport>('GET', `/api/project/health?${q(path)}`),
  upgradePlan: (path: string, req: UpgradeRequest) => request<UpgradePlan>('POST', `/api/project/upgrade/plan?${q(path)}`, req),
  upgrade: (path: string, req: UpgradeRequest) => request<ChangeResult>('POST', `/api/project/upgrade?${q(path)}`, req),
  runStatus: (path: string) => request<RunStatus>('GET', `/api/project/run?${q(path)}`),
  run: (path: string) => request<RunStatus>('POST', `/api/project/run?${q(path)}`),
  stop: (path: string) => request<RunStatus>('POST', `/api/project/stop?${q(path)}`),
  tryEndpoint: (path: string, req: TryRequest) => request<TryResponse>('POST', `/api/project/try?${q(path)}`, req),
  postmanUrl: (path: string, baseUrl?: string | null) => `/api/project/postman?${q(path)}${baseUrl ? `&baseUrl=${encodeURIComponent(baseUrl)}` : ''}`,

  dbConnect: (info: DbConnectionInfo) => request<{ version: string; databases: string[] }>('POST', '/api/db/connect', info),
  dbTables: (info: DbConnectionInfo) => request<DbTable[]>('POST', '/api/db/tables', info),
  generate: (req: GenerateProjectRequest) => request<GenerateProjectResult>('POST', '/api/db/generate', req),
}
