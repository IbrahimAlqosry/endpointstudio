import { useState } from 'react'
import { api } from '../api'
import type { EndpointInfo, EndpointParam, ProjectDetails, RunStatus, TryResponse } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Drawer, Field, MethodBadge, RouteText, errorText, useToast } from './ui'

const WHERE: Record<string, { label: string; hint: string }> = {
  Route: { label: 'Path', hint: 'Part of the address' },
  Query: { label: 'Query', hint: 'After ? in the address' },
  Body: { label: 'Body', hint: 'Sent as JSON' },
  Header: { label: 'Header', hint: 'Sent as a header' },
  Service: { label: 'App', hint: 'Provided by the app' },
}

export function EndpointDrawer({ project, endpoint, run, tab, onTab, onClose, onEdit, onDelete, onCopy, onStart }: {
  project: ProjectDetails
  endpoint: EndpointInfo
  run: RunStatus | null
  tab: 'overview' | 'try'
  onTab: (t: 'overview' | 'try') => void
  onClose: () => void
  onEdit: () => void
  onDelete: () => void
  onCopy: () => void
  onStart: () => void
}) {
  const toast = useToast()
  const group = project.groups.find(g => g.key === endpoint.groupKey)
  const inputs = endpoint.parameters.filter(p => p.source !== 'Service')
  const provided = endpoint.parameters.filter(p => p.source === 'Service')
  const body = inputs.find(p => p.source === 'Body')
  const [svcName, opName] = endpoint.calls?.split('.') ?? []
  const service = project.services.find(s => s.interface === svcName)
  const baseUrl = run?.state === 'running' && run.url ? run.url : 'https://your-server'
  const exampleUrl = baseUrl + exampleRoute(endpoint)
  const fromLibrary = endpoint.project !== project.name
  const filePath = joinPath(project.directory, endpoint.file)

  const copy = (text: string, what: string) => { navigator.clipboard?.writeText(text); toast.success(`${what} copied.`) }

  return (
    <Drawer onClose={onClose}>
      <div className="drawer-header">
        <div className="row between">
          <div className="row gap-6 wrap">
            <MethodBadge method={endpoint.httpMethod} />
            {endpoint.requiresAuth ? <span className="badge warning"><Icon name="lock" size={12} /> Sign-in required</span> : <span className="badge"><Icon name="unlock" size={12} /> Public</span>}
          </div>
          <Button size="sm" variant="ghost" iconOnly icon="x" onClick={onClose} aria-label="Close" />
        </div>
        <div style={{ marginTop: 12, fontSize: 17 }}><RouteText route={endpoint.route} /></div>
        <div className="strong mt-8" style={{ fontSize: 15 }}>{endpoint.name ? splitWords(endpoint.name) : <span className="subtle">Unnamed endpoint</span>}</div>
        {endpoint.summary ? <p className="muted small" style={{ marginTop: 2 }}>{endpoint.summary}</p> : <p className="subtle small" style={{ marginTop: 2 }}>No description yet. <a href="#" onClick={e => { e.preventDefault(); onEdit() }}>Add one</a></p>}
        <div className="row wrap gap-6 mt-16">
          <Button size="sm" variant="primary" icon="send" onClick={() => onTab('try')}>Try it</Button>
          <Button size="sm" icon="copy" onClick={onCopy}>Make a copy</Button>
          <Button size="sm" icon="edit" onClick={onEdit}>Edit</Button>
          <Button size="sm" variant="ghost" icon="trash" onClick={onDelete}>Delete</Button>
        </div>
        <div className="tabs">
          <button className={`tab${tab === 'overview' ? ' active' : ''}`} onClick={() => onTab('overview')}>Details</button>
          <button className={`tab${tab === 'try' ? ' active' : ''}`} onClick={() => onTab('try')}>Try it</button>
        </div>
      </div>

      <div className="drawer-body">
        {tab === 'overview' ? (
          <div className="col gap-20">
            {/* Request */}
            <Card icon="send" title="Request">
              <div className="row" style={{ padding: '8px 10px', background: 'var(--bg-sunken)', borderRadius: 8 }}>
                <MethodBadge method={endpoint.httpMethod} />
                <span className="mono small grow" style={{ wordBreak: 'break-all' }}>{exampleUrl}</span>
                <Button size="sm" variant="ghost" iconOnly icon="copy" title="Copy address" onClick={() => copy(exampleUrl, 'Address')} />
              </div>
              {inputs.length === 0 ? <p className="small subtle mt-8">No inputs: call it as it is.</p> : (
                <div className="table-wrap mt-8">
                  <table className="table">
                    <thead><tr><th>Input</th><th>Type</th><th>Where</th><th>Required</th></tr></thead>
                    <tbody>
                      {inputs.map(p => (
                        <tr key={p.name}>
                          <td className="strong mono" style={{ fontSize: 12.5 }}>{p.name}</td>
                          <td className="small">{p.type}</td>
                          <td className="small" title={WHERE[p.source]?.hint}><span className="badge">{WHERE[p.source]?.label ?? p.source}</span></td>
                          <td>{p.optional ? <span className="small subtle">Optional</span> : <span className="small strong">Required</span>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              {provided.length > 0 && <p className="tiny subtle mt-8">Also uses {provided.map(p => p.type).join(', ')}, provided automatically by the app.</p>}
              {body && (
                <div className="mt-8">
                  <div className="row between"><span className="label">Example body</span><Button size="sm" variant="ghost" icon="copy" onClick={() => copy(sampleBody(project, body, endpoint), 'Example body')}>Copy</Button></div>
                  <pre className="response-body" style={{ maxHeight: 320 }}>{sampleBody(project, body, endpoint)}</pre>
                </div>
              )}
            </Card>

            {/* Response */}
            <Card icon="arrowLeft" title="Response">
              <dl className="kv">
                <dt>Returns</dt><dd>{endpoint.returns}</dd>
                <dt>Possible results</dt>
                <dd className="row wrap gap-4">
                  {statusCodes(endpoint).map(([code, text]) => <span key={code} className={`badge ${code < 300 ? 'success' : code < 500 ? 'warning' : 'danger'}`} title={text}>{code} {text}</span>)}
                </dd>
              </dl>
            </Card>

            {/* Behind the scenes */}
            <Card icon="layers" title="Behind the scenes">
              <dl className="kv">
                {endpoint.calls && <>
                  <dt>Calls</dt>
                  <dd><span className="strong">{splitWords(opName)}</span> <span className="subtle">in {splitWords((svcName ?? '').replace(/^I(?=[A-Z])/, ''))}{service ? ` · ${service.project}` : ''}</span></dd>
                </>}
                <dt>Group</dt><dd>{group?.name ?? '—'} <span className="subtle mono small">{group?.baseRoute}</span></dd>
                <dt>Access</dt><dd>{endpoint.requiresAuth ? 'Only signed-in users' : 'Anyone'}</dd>
                <dt>Style</dt><dd>{endpoint.style === 'Controller' ? (project.capabilities.platform === 'NetFramework' ? 'Web API controller' : 'Controller action') : 'Minimal API'}</dd>
                <dt>Project</dt><dd>{endpoint.project}{fromLibrary && <span className="badge" style={{ marginLeft: 6 }}>library used by this API</span>}</dd>
                <dt>Location</dt>
                <dd className="row gap-6 wrap">
                  <span className="small">{endpoint.file.split('/').pop()} <span className="subtle">· line {endpoint.line}</span></span>
                  <button className="btn ghost sm" onClick={() => api.openEditor(filePath, endpoint.line).catch(e => toast.error(errorText(e)))}><Icon name="code" size={13} />Open in VS Code</button>
                </dd>
              </dl>
            </Card>
          </div>
        ) : (
          <TryPanel project={project} endpoint={endpoint} run={run} onStart={onStart} />
        )}
      </div>
    </Drawer>
  )
}

function Card({ icon, title, children }: { icon: string; title: string; children: React.ReactNode }) {
  return (
    <section className="card pad" style={{ padding: 14 }}>
      <div className="row gap-6" style={{ marginBottom: 10 }}><Icon name={icon} size={15} /><span className="strong">{title}</span></div>
      {children}
    </section>
  )
}

function TryPanel({ project, endpoint, run, onStart }: { project: ProjectDetails; endpoint: EndpointInfo; run: RunStatus | null; onStart: () => void }) {
  const params = endpoint.parameters.filter(p => p.source === 'Route' || p.source === 'Query' || p.source === 'Header')
  const bodyParam = endpoint.parameters.find(p => p.source === 'Body')
  const [values, setValues] = useState<Record<string, string>>({})
  const [body, setBody] = useState(() => bodyParam ? sampleBody(project, bodyParam, endpoint) : '')
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
        path = path.replace(new RegExp(`\\{\\*{0,2}${p.name}[^}]*\\}`, 'i'), encodeURIComponent(values[p.name] ?? ''))
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
        <Alert kind="info">To try endpoints, the API needs to be running. Endpoint Studio will start it for you.</Alert>
        {project.capabilities.canRun
          ? <Button variant="success" icon="play" loading={run?.state === 'starting'} onClick={onStart}>{run?.state === 'starting' ? 'Starting…' : 'Run the API'}</Button>
          : <Alert kind="warning">{project.capabilities.runNote}</Alert>}
      </div>
    )
  }

  const missing = params.filter(p => p.source === 'Route' && !values[p.name])
  return (
    <div className="col gap-16">
      {params.map(p => (
        <Field key={p.name} label={<>{p.name} <span className="subtle" style={{ fontWeight: 400 }}>· {p.type} · {WHERE[p.source]?.label.toLowerCase()}{p.optional ? ' · optional' : ''}</span></>}>
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
      <Button variant="primary" icon="send" loading={sending} onClick={send} disabled={missing.length > 0} title={missing.length ? 'Fill in the path values first' : undefined}>Send request</Button>
      {error && <Alert kind="error">{error}</Alert>}
      {response && (
        <div className="col">
          <div className="row">
            <span className={`badge ${response.status < 300 ? 'success' : response.status < 400 ? 'accent' : response.status < 500 ? 'warning' : 'danger'}`} style={{ height: 26, fontSize: 13 }}>{response.status} {response.statusText}</span>
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

// ---------------------------------------------------------------- helpers

function exampleRoute(e: EndpointInfo) {
  let path = e.route.replace(/\{\*{0,2}([A-Za-z_]\w*)[^}]*\}/g, (_, n: string) => {
    const p = e.parameters.find(x => x.name.toLowerCase() === n.toLowerCase())
    return p ? exampleValue(p) : '1'
  })
  const q = e.parameters.filter(p => p.source === 'Query' && !p.optional).map(p => `${p.name}=${exampleValue(p)}`)
  if (q.length) path += '?' + q.join('&')
  return path
}

function exampleValue(p: EndpointParam) {
  switch (p.type) {
    case 'Whole number': return '1'
    case 'Decimal number': return '9.99'
    case 'Yes / No': return 'true'
    case 'Unique ID': return '3fa85f64-5717-4562-b3fc-2c963f66afa6'
    case 'Date': return '2026-01-31'
    default: return p.name.toLowerCase().includes('id') ? '1' : 'value'
  }
}

function statusCodes(e: EndpointInfo): [number, string][] {
  const codes: [number, string][] = []
  const m = e.httpMethod
  codes.push(m === 'DELETE' && /^(Delete|Remove)/.test(e.name) ? [204, 'Done'] : m === 'POST' && /^(Create|Add|Insert)/.test(e.name) ? [201, 'Created'] : [200, 'OK'])
  if (e.parameters.some(p => p.source === 'Body' || (!p.optional && p.source !== 'Service'))) codes.push([400, 'Invalid input'])
  if (e.requiresAuth) codes.push([401, 'Not signed in'])
  if (e.parameters.some(p => p.source === 'Route')) codes.push([404, 'Not found'])
  codes.push([500, 'Server error'])
  return codes
}

function sampleBody(project: ProjectDetails, p: EndpointParam, endpoint?: EndpointInfo): string {
  const entity = project.entities.find(e => e.name === p.type)
  if (!entity) return endpoint?.bodySample ?? '{\n  \n}'
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

function joinPath(dir: string, rel: string) {
  const sep = dir.includes('\\') ? '\\' : '/'
  return dir.replace(/[\\/]+$/, '') + sep + rel.replace(/\//g, sep)
}

function splitWords(s: string) {
  return (s ?? '').replace(/([a-z0-9])([A-Z])/g, '$1 $2')
}
