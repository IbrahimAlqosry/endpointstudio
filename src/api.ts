import type {
  AddEndpointRequest, BrowseResult, SystemInfo, BuildResult, ChangeResult, DbConnectionInfo, DbTable, GenerateProjectRequest,
  DeployProfile, DeployRequest, FrontendDetails, InspectResult, RunStatus as FeRunStatus, DeployTools, GenerateProjectResult, HealthReport, JobStatus, SettingChange, SettingsInfo, SqlTestRequest, SqlTestResult, ProjectDetails, SolutionDetails, UpgradePlan, UpgradeRequest, ProjectFileEntry, RecentProject, RunStatus, TryRequest, TryResponse, UpdateEndpointRequest,
  GitBranch, GitCommit, GitCommitDetails, GitDiff, GitStash, GitStatus,
  DbRef, DbSchema, DdlChange, RowsResult, SavedConnection, SqlResult,
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

  // Database Studio
  dbsConnections: () => request<SavedConnection[]>('GET', '/api/dbs/connections'),
  dbsSaveConnection: (id: string | null, name: string, info: DbConnectionInfo) => request<SavedConnection>('POST', '/api/dbs/connections', { id, name, info }),
  dbsDeleteConnection: (id: string) => request<void>('DELETE', `/api/dbs/connections/${encodeURIComponent(id)}`),
  dbsConnect: (ref: DbRef) => request<{ version: string; databases: string[] }>('POST', '/api/dbs/connect', ref),
  dbsSchema: (ref: DbRef) => request<DbSchema>('POST', '/api/dbs/schema', ref),
  dbsScript: (ref: DbRef, schema: string, table: string) => request<{ text: string }>('POST', '/api/dbs/script', { ref, schema, table }).then(r => r.text),
  dbsRoutine: (ref: DbRef, schema: string, name: string, kind: string) => request<{ text: string }>('POST', '/api/dbs/routine', { ref, schema, name, kind }).then(r => r.text),
  dbsRows: (ref: DbRef, schema: string, table: string, o: { offset: number; limit: number; orderBy?: string | null; desc?: boolean; search?: string; filters?: Record<string, string> }) =>
    request<RowsResult>('POST', '/api/dbs/rows', { ref, schema, table, ...o, desc: !!o.desc }),
  dbsInsert: (ref: DbRef, schema: string, table: string, values: Record<string, string | null>) => request<{ affected: number }>('POST', '/api/dbs/rows/insert', { ref, schema, table, values }),
  dbsUpdate: (ref: DbRef, schema: string, table: string, keys: Record<string, string | null>, values: Record<string, string | null>) => request<{ affected: number }>('POST', '/api/dbs/rows/update', { ref, schema, table, keys, values }),
  dbsDelete: (ref: DbRef, schema: string, table: string, keyList: Record<string, string | null>[]) => request<{ affected: number }>('POST', '/api/dbs/rows/delete', { ref, schema, table, keyList }),
  dbsImport: (ref: DbRef, schema: string, table: string, rows: Record<string, string | null>[]) => request<{ affected: number; ignoredColumns: string[] }>('POST', '/api/dbs/rows/import', { ref, schema, table, rows }),
  dbsExport: async (ref: DbRef, schema: string, table: string, format: string, search?: string) => {
    const res = await fetch('/api/dbs/rows/export', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ref, schema, table, format, search }) })
    if (!res.ok) { let m = 'Export failed.'; try { m = (await res.json()).error ?? m } catch { /* ignore */ } throw new ApiError(m) }
    return res.blob()
  },
  dbsPreview: (ref: DbRef, change: DdlChange) => request<{ statements: string[]; warning: string | null }>('POST', '/api/dbs/ddl/preview', { ref, change }),
  dbsApply: (ref: DbRef, change: DdlChange) => request<{ statements: string[] }>('POST', '/api/dbs/ddl/apply', { ref, change }),
  dbsSql: (ref: DbRef, sql: string, dryRun: boolean, confirmed: boolean) => request<SqlResult>('POST', '/api/dbs/sql', { ref, sql, dryRun, confirmed }),

  // Source control
  gitStatus: (path: string) => request<GitStatus>('GET', `/api/git/status?${q(path)}`),
  gitInit: (path: string) => request<GitStatus>('POST', '/api/git/init', { path }),
  gitIgnore: (path: string) => request<void>('POST', '/api/git/gitignore', { path }),
  gitIdentity: (path: string, name: string, email: string, global: boolean) => request<void>('POST', '/api/git/identity', { path, name, email, global }),
  gitTrust: (path: string) => request<void>('POST', '/api/git/trust', { path }),
  gitSetRemote: (path: string, name: string, url: string) => request<void>('POST', '/api/git/remote', { path, name, url }),
  gitRemoveRemote: (path: string, name: string) => request<void>('POST', '/api/git/remote/remove', { path, name, url: '' }),
  gitCommit: (path: string, message: string, files: string[] | null, amend = false) => request<{ hash: string }>('POST', '/api/git/commit', { path, message, files, amend }),
  gitDiscard: (path: string, files: string[]) => request<void>('POST', '/api/git/discard', { path, files }),
  gitDiff: (path: string, file: string, commit?: string) => request<GitDiff>('GET', `/api/git/diff?${q(path)}&file=${encodeURIComponent(file)}${commit ? `&commit=${commit}` : ''}`),
  gitResolve: (path: string, file: string, keep: 'mine' | 'theirs' | 'edited') => request<void>('POST', '/api/git/resolve', { path, file, keep }),
  gitAbort: (path: string) => request<void>('POST', '/api/git/abort', { path }),
  gitLog: (path: string, skip = 0, search?: string, branch?: string) => request<GitCommit[]>('GET', `/api/git/log?${q(path)}&skip=${skip}${search ? `&search=${encodeURIComponent(search)}` : ''}${branch ? `&branch=${encodeURIComponent(branch)}` : ''}`),
  gitShow: (path: string, hash: string) => request<GitCommitDetails>('GET', `/api/git/show?${q(path)}&hash=${hash}`),
  gitRevert: (path: string, ref: string) => request<void>('POST', '/api/git/revert', { path, ref }),
  gitUndoCommit: (path: string) => request<void>('POST', '/api/git/undo-commit', { path }),
  gitTag: (path: string, ref: string, name: string) => request<void>('POST', '/api/git/tag', { path, ref, message: name }),
  gitBranches: (path: string) => request<GitBranch[]>('GET', `/api/git/branches?${q(path)}`),
  gitCreateBranch: (path: string, name: string, from?: string, checkout = true) => request<void>('POST', '/api/git/branch/create', { path, name, from, checkout }),
  gitCheckout: (path: string, name: string) => request<void>('POST', '/api/git/branch/checkout', { path, name }),
  gitDeleteBranch: (path: string, name: string, force = false) => request<void>('POST', '/api/git/branch/delete', { path, name, force }),
  gitRenameBranch: (path: string, from: string, name: string) => request<void>('POST', '/api/git/branch/rename', { path, name, from }),
  gitMerge: (path: string, name: string) => request<{ conflicts: boolean; message: string }>('POST', '/api/git/merge', { path, name }),
  gitStashes: (path: string) => request<GitStash[]>('GET', `/api/git/stashes?${q(path)}`),
  gitStash: (path: string, message: string) => request<void>('POST', '/api/git/stash', { path, ref: '', message }),
  gitStashPop: (path: string, ref: string) => request<void>('POST', '/api/git/stash/pop', { path, ref }),
  gitStashApply: (path: string, ref: string) => request<void>('POST', '/api/git/stash/apply', { path, ref }),
  gitStashDrop: (path: string, ref: string) => request<void>('POST', '/api/git/stash/drop', { path, ref }),
  gitFetch: (path: string) => request<{ jobId: string }>('POST', '/api/git/fetch', { path }),
  gitPull: (path: string) => request<{ jobId: string }>('POST', '/api/git/pull', { path }),
  gitPush: (path: string) => request<{ jobId: string }>('POST', '/api/git/push', { path }),
  gitClone: (url: string, folder: string) => request<{ jobId: string }>('POST', '/api/git/clone', { url, folder }),
}
