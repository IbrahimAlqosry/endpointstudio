import type {
  AddEndpointRequest, BrowseResult, BuildResult, ChangeResult, DbConnectionInfo, DbTable, GenerateProjectRequest,
  GenerateProjectResult, ProjectDetails, ProjectFileEntry, RecentProject, RunStatus, TryRequest, TryResponse, UpdateEndpointRequest,
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
    throw new ApiError('Endpoint Studio’s helper service isn’t running. Start it with “npm run dev” and try again.')
  }
  const text = await res.text()
  let data: unknown = null
  try { data = text ? JSON.parse(text) : null } catch { /* non-json */ }
  if (!res.ok) {
    const msg = (data as { error?: string } | null)?.error
    throw new ApiError(msg ?? (res.status === 502 || res.status === 504
      ? 'Endpoint Studio’s helper service isn’t running. Start it with “npm run dev” and try again.'
      : `Request failed (${res.status}).`))
  }
  return data as T
}

const q = (path: string) => `path=${encodeURIComponent(path)}`

export const api = {
  system: () => request<{ dotnetVersion: string; defaultLocation: string }>('GET', '/api/system'),

  browse: (path?: string | null) => request<BrowseResult>('GET', `/api/fs/browse${path ? `?${q(path)}` : ''}`),
  findProjects: (root: string) => request<ProjectFileEntry[]>('GET', `/api/fs/find-projects?root=${encodeURIComponent(root)}`),
  reveal: (path: string) => request<void>('POST', '/api/fs/reveal', { path }),
  openEditor: (path: string) => request<void>('POST', '/api/fs/open-editor', { path }),

  recent: () => request<RecentProject[]>('GET', '/api/projects/recent'),
  forget: (path: string) => request<void>('DELETE', `/api/projects/recent?${q(path)}`),

  project: (path: string) => request<ProjectDetails>('GET', `/api/project?${q(path)}`),
  addEndpoint: (path: string, req: AddEndpointRequest) => request<ChangeResult>('POST', `/api/project/endpoints?${q(path)}`, req),
  updateEndpoint: (path: string, id: string, req: UpdateEndpointRequest) => request<ChangeResult>('PUT', `/api/project/endpoints/${encodeURIComponent(id)}?${q(path)}`, req),
  deleteEndpoint: (path: string, id: string) => request<ChangeResult>('DELETE', `/api/project/endpoints/${encodeURIComponent(id)}?${q(path)}`),
  undo: (path: string) => request<ChangeResult>('POST', `/api/project/undo?${q(path)}`),
  build: (path: string) => request<BuildResult>('POST', `/api/project/build?${q(path)}`),
  runStatus: (path: string) => request<RunStatus>('GET', `/api/project/run?${q(path)}`),
  run: (path: string) => request<RunStatus>('POST', `/api/project/run?${q(path)}`),
  stop: (path: string) => request<RunStatus>('POST', `/api/project/stop?${q(path)}`),
  tryEndpoint: (path: string, req: TryRequest) => request<TryResponse>('POST', `/api/project/try?${q(path)}`, req),
  postmanUrl: (path: string, baseUrl?: string | null) => `/api/project/postman?${q(path)}${baseUrl ? `&baseUrl=${encodeURIComponent(baseUrl)}` : ''}`,

  dbConnect: (info: DbConnectionInfo) => request<{ version: string; databases: string[] }>('POST', '/api/db/connect', info),
  dbTables: (info: DbConnectionInfo) => request<DbTable[]>('POST', '/api/db/tables', info),
  generate: (req: GenerateProjectRequest) => request<GenerateProjectResult>('POST', '/api/db/generate', req),
}
