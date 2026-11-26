import { useState } from 'react'
import { api } from '../api'
import type { EndpointInfo, EndpointParam, ProjectDetails, RunStatus, TryResponse } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Drawer, Field, MethodBadge, RouteText, errorText } from './ui'

const SOURCE_LABEL: Record<string, string> = { Route: 'In the path', Query: 'In the query string', Body: 'In the body', Header: 'In a header', Service: 'Provided by the app' }

export function EndpointDrawer({ project, endpoint, run, tab, onTab, onClose, onEdit, onDelete, onStart }: {
  project: ProjectDetails
  endpoint: EndpointInfo
  run: RunStatus | null
  tab: 'overview' | 'try'
  onTab: (t: 'overview' | 'try') => void
  onClose: () => void
  onEdit: () => void
  onDelete: () => void
  onStart: () => void
}) {
  const group = project.groups.find(g => g.key === endpoint.groupKey)
  const inputs = endpoint.parameters.filter(p => p.source !== 'Service')

  return (
    <Drawer onClose={onClose}>
      <div className="drawer-header">
        <div className="row between">
          <div className="row gap-6"><MethodBadge method={endpoint.httpMethod} />{endpoint.requiresAuth && <span className="badge warning"><Icon name="lock" size={12} /> Sign-in required</span>}</div>
          <div className="row gap-4">
            <Button size="sm" variant="ghost" icon="edit" onClick={onEdit}>Edit</Button>
            <Button size="sm" variant="ghost" icon="trash" onClick={onDelete}>Delete</Button>
            <Button size="sm" variant="ghost" iconOnly icon="x" onClick={onClose} aria-label="Close" />
          </div>
        </div>
        <div style={{ marginTop: 12, fontSize: 16 }}><RouteText route={endpoint.route} /></div>
        {endpoint.summary && <p className="muted mt-8">{endpoint.summary}</p>}
        <div className="tabs">
          <button className={`tab${tab === 'overview' ? ' active' : ''}`} onClick={() => onTab('overview')}>Overview</button>
          <button className={`tab${tab === 'try' ? ' active' : ''}`} onClick={() => onTab('try')}>Try it</button>
        </div>
      </div>
      <div className="drawer-body">
        {tab === 'overview' ? (
          <div className="col gap-20">
            <dl className="kv">
              <dt>Name</dt><dd className="strong">{endpoint.name ? splitWords(endpoint.name) : <span className="subtle">Not named</span>}</dd>
              <dt>Group</dt><dd>{group?.name ?? '—'}</dd>
              <dt>Full path</dt><dd><RouteText route={endpoint.route} /></dd>
              <dt>Returns</dt><dd>{endpoint.returns}</dd>
              <dt>Access</dt><dd>{endpoint.requiresAuth ? 'Only signed-in users' : 'Anyone'}</dd>
              <dt>Style</dt><dd>{endpoint.style === 'Controller' ? 'Controller action' : 'Minimal API'}</dd>
            </dl>

            <div>
              <div className="label" style={{ marginBottom: 8 }}>Inputs</div>
              {inputs.length === 0 ? <p className="small subtle">This endpoint doesn’t need any input.</p> : (
                <div className="table-wrap">
                  <table className="table">
                    <thead><tr><th>Name</th><th>Type</th><th>Where</th><th></th></tr></thead>
                    <tbody>
                      {inputs.map(p => (
                        <tr key={p.name}>
                          <td className="strong">{p.name}</td>
                          <td>{p.type}</td>
                          <td className="small muted">{SOURCE_LABEL[p.source]}</td>
                          <td>{p.optional ? <span className="badge">Optional</span> : <span className="badge accent">Required</span>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>

            <details className="disclose">
              <summary><Icon name="chevronRight" size={13} /> Where it lives in the project</summary>
              <div className="small muted">{endpoint.file.split('/').pop()} <span className="subtle">· line {endpoint.line}</span></div>
            </details>

            <Button variant="primary" icon="send" onClick={() => onTab('try')}>Try this endpoint</Button>
          </div>
        ) : (
          <TryPanel project={project} endpoint={endpoint} run={run} onStart={onStart} />
        )}
      </div>
    </Drawer>
  )
}

function TryPanel({ project, endpoint, run, onStart }: { project: ProjectDetails; endpoint: EndpointInfo; run: RunStatus | null; onStart: () => void }) {
  const params = endpoint.parameters.filter(p => p.source === 'Route' || p.source === 'Query' || p.source === 'Header')
  const bodyParam = endpoint.parameters.find(p => p.source === 'Body')
  const [values, setValues] = useState<Record<string, string>>({})
  const [body, setBody] = useState(() => bodyParam ? sampleBody(project, bodyParam) : '')
  const [auth, setAuth] = useState('')
  const [sending, setSending] = useState(false)
  const [response, setResponse] = useState<TryResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  const running = run?.state === 'running'

  const send = async () => {
    setSending(true); setError(null)
    try {
      let path = endpoint.route
      for (const p of params.filter(p => p.source === 'Route')) {
        const v = encodeURIComponent(values[p.name] ?? '')
        path = path.replace(new RegExp(`\\{\\*{0,2}${p.name}[^}]*\\}`, 'i'), v)
      }
      path = path.replace(/\{[^}]+\}/g, '')
      const qs = new URLSearchParams()
      for (const p of params.filter(p => p.source === 'Query')) if (values[p.name]) qs.set(p.name, values[p.name])
      if ([...qs].length) path += '?' + qs.toString()
      const headers: Record<string, string> = {}
      for (const p of params.filter(p => p.source === 'Header')) if (values[p.name]) headers[p.name] = values[p.name]
      if (auth.trim()) headers['Authorization'] = auth.trim().toLowerCase().startsWith('bearer ') ? auth.trim() : `Bearer ${auth.trim()}`
      if (bodyParam && body.trim()) {
        try { JSON.parse(body) } catch { throw new Error('The body isn’t valid JSON. Check for missing quotes or commas.') }
      }
      setResponse(await api.tryEndpoint(project.path, { method: endpoint.httpMethod === 'ANY' ? 'GET' : endpoint.httpMethod, path, headers, body: bodyParam ? body : null }))
    } catch (e) { setError(errorText(e)) } finally { setSending(false) }
  }

  if (!running) {
    return (
      <div className="col gap-16">
        <Alert kind="info">To try endpoints, your API needs to be running. Endpoint Studio will start it for you.</Alert>
        <Button variant="success" icon="play" loading={run?.state === 'starting'} onClick={onStart}>{run?.state === 'starting' ? 'Starting…' : 'Run the API'}</Button>
      </div>
    )
  }

  const missing = params.filter(p => p.source === 'Route' && !values[p.name])

  return (
    <div className="col gap-16">
      {params.map(p => (
        <Field key={p.name} label={<>{p.name} <span className="subtle" style={{ fontWeight: 400 }}>· {p.type} · {p.source === 'Route' ? 'path' : p.source === 'Query' ? 'query' : 'header'}{p.optional ? ' · optional' : ''}</span></>}>
          <input className="input" value={values[p.name] ?? ''} onChange={e => setValues(v => ({ ...v, [p.name]: e.target.value }))} placeholder={placeholderFor(p)} />
        </Field>
      ))}
      {bodyParam && (
        <Field label={<>Body <span className="subtle" style={{ fontWeight: 400 }}>· {bodyParam.type} as JSON</span></>} hint="Fill in the values you want to send.">
          <textarea className="textarea code" rows={10} value={body} onChange={e => setBody(e.target.value)} spellCheck={false} />
        </Field>
      )}
      {endpoint.requiresAuth && (
        <Field label="Access token" hint="This endpoint requires sign-in. Paste a token to send with the request.">
          <input className="input" value={auth} onChange={e => setAuth(e.target.value)} placeholder="Bearer eyJhbGciOi…" />
        </Field>
      )}

      <Button variant="primary" icon="send" loading={sending} onClick={send} disabled={missing.length > 0} title={missing.length ? 'Fill in the path values first' : undefined}>
        Send request
      </Button>

      {error && <Alert kind="error">{error}</Alert>}

      {response && (
        <div className="col">
          <div className="row">
            <span className={`badge ${response.status < 300 ? 'success' : response.status < 400 ? 'accent' : response.status < 500 ? 'warning' : 'danger'}`} style={{ height: 26, fontSize: 13 }}>
              {response.status} {response.statusText}
            </span>
            <span className="small subtle">{response.durationMs} ms</span>
            <span className="grow" />
            <Button size="sm" variant="ghost" icon="copy" onClick={() => navigator.clipboard?.writeText(response.body)}>Copy</Button>
          </div>
          <p className="small muted">{explainStatus(response.status)}</p>
          {response.body ? <pre className="response-body">{response.body}</pre> : <p className="small subtle">The response has no content.</p>}
        </div>
      )}
    </div>
  )
}

function sampleBody(project: ProjectDetails, p: EndpointParam): string {
  const entity = project.entities.find(e => e.name === p.type)
  if (!entity) return '{\n  \n}'
  const obj: Record<string, unknown> = {}
  for (const prop of entity.properties) {
    obj[prop.name] = prop.type === 'string' ? '' :
      ['int', 'long', 'short', 'byte', 'decimal', 'double', 'float', 'uint', 'ulong', 'sbyte', 'ushort'].includes(prop.type) ? 0 :
        prop.type === 'bool' ? false :
          prop.type === 'Guid' ? '00000000-0000-0000-0000-000000000000' :
            prop.type === 'DateTime' || prop.type === 'DateTimeOffset' ? new Date().toISOString().slice(0, 19) :
              prop.type === 'DateOnly' ? new Date().toISOString().slice(0, 10) :
                prop.type === 'TimeOnly' || prop.type === 'TimeSpan' ? '00:00:00' : null
  }
  return JSON.stringify(obj, null, 2)
}

function placeholderFor(p: EndpointParam) {
  switch (p.type) {
    case 'Whole number': return 'e.g. 1'
    case 'Decimal number': return 'e.g. 9.99'
    case 'Yes / No': return 'true or false'
    case 'Unique ID': return 'e.g. 3fa85f64-5717-4562-b3fc-2c963f66afa6'
    case 'Date': return 'e.g. 2026-01-31'
    default: return ''
  }
}

function explainStatus(s: number) {
  if (s === 200) return 'Success: the request worked.'
  if (s === 201) return 'Created: the new item was saved.'
  if (s === 204) return 'Success: done, with nothing to return.'
  if (s === 400) return 'Bad request: some of the input values aren’t valid.'
  if (s === 401) return 'Not signed in: this endpoint needs a valid access token.'
  if (s === 403) return 'Forbidden: you don’t have permission for this.'
  if (s === 404) return 'Not found: nothing exists at this path or with this ID.'
  if (s === 405) return 'Method not allowed: this path doesn’t accept this HTTP method.'
  if (s === 415) return 'Unsupported body: the body format wasn’t accepted.'
  if (s === 501) return 'Not implemented yet.'
  if (s >= 500) return 'Server error: something failed inside the API.'
  return ''
}

function splitWords(s: string) {
  return s.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
}
