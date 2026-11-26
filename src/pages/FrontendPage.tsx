import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { GitPanel } from '../components/GitPanel'
import { DeployPanel } from '../components/DeployPanel'
import { FolderPicker } from '../components/FolderPicker'
import { HealthDialog } from '../components/HealthDialog'
import { HistoryDialog } from '../components/HistoryDialog'
import { Icon } from '../components/Icon'
import { JobView } from '../components/JobView'
import { SettingsPanel } from '../components/SettingsPanel'
import { Alert, Button, Empty, MethodBadge, Spinner, errorText, useToast } from '../components/ui'
import { href, navigate } from '../router'
import type { FrontendApiCall, FrontendDetails, RunStatus } from '../types'

type Tab = 'calls' | 'pages' | 'components' | 'settings' | 'deploy' | 'git'

const MATCH: Record<FrontendApiCall['match'], { label: string; cls: string; hint: string }> = {
  matched: { label: 'Found in API', cls: 'success', hint: 'This call reaches an endpoint in the connected API.' },
  missing: { label: 'Not in API', cls: 'danger', hint: 'No endpoint in the connected API has this path: the call will likely fail with 404.' },
  method: { label: 'Wrong method', cls: 'warning', hint: 'The path exists, but the API expects a different HTTP method.' },
  unknown: { label: "Can't check", cls: '', hint: 'The address is built entirely at run time, so it can’t be checked here.' },
  asset: { label: 'App file', cls: '', hint: 'Loads a file from the app itself (translations, settings…), not the API.' },
}

const FRAMEWORK_ICON: Record<string, string> = { Angular: 'layers', React: 'sparkles', 'Next.js': 'zap', Vue: 'box' }

export function FrontendPage({ path, onChanged }: { path: string; onChanged: () => void }) {
  const toast = useToast()
  const [app, setApp] = useState<FrontendDetails | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('calls')
  const [run, setRun] = useState<RunStatus | null>(null)
  const [job, setJob] = useState<string | null>(null)
  const [healthOpen, setHealthOpen] = useState(false)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [picker, setPicker] = useState(false)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setApp(await api.frontend(path)); setError(null); onChanged() } catch (e) { setError(errorText(e)) }
  }, [path]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { load() }, [load])

  useEffect(() => {
    let alive = true
    let timer: number
    const tick = async () => {
      try {
        const s = await api.frontendRunStatus(path)
        if (!alive) return
        setRun(s)
        timer = window.setTimeout(tick, s.state === 'starting' ? 1200 : 4000)
      } catch { timer = window.setTimeout(tick, 8000) }
    }
    tick()
    return () => { alive = false; clearTimeout(timer) }
  }, [path])

  const link = async (backend: string | null) => {
    try { setApp(await api.frontendLink(path, backend)); setPicker(false); toast.success(backend ? 'Connected. Every API call is now checked against that project.' : 'Disconnected from the API project.') }
    catch (e) { toast.error(errorText(e)) }
  }

  const toggleRun = async () => {
    setBusy(true)
    try {
      if (run?.running) { setRun(await api.frontendStop(path)); toast.info('The app was stopped.') }
      else { setRun(await api.frontendRun(path)); toast.info('Starting the app… the first start can take a little while.') }
    } catch (e) { toast.error(errorText(e)) } finally { setBusy(false) }
  }

  if (error) return <div className="page"><div className="card"><Empty icon="alert" title="This app couldn't be opened" action={<a className="btn" href={href({ page: 'home' })}>Back to home</a>}>{error}</Empty></div></div>
  if (!app) return <div className="page"><div className="empty"><Spinner large /><span className="muted">Reading the app…</span></div></div>

  const counts = {
    matched: app.apiCalls.filter(c => c.match === 'matched').length,
    problems: app.apiCalls.filter(c => c.match === 'missing' || c.match === 'method').length,
  }
  const pages = app.routes.filter(r => r.kind !== 'redirect' && r.kind !== 'api').length

  return (
    <div className="page">
      <div className="page-header">
        <div className="grow" style={{ minWidth: 260 }}>
          <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>{app.name}</span></div>
          <div className="row wrap">
            <h1 className="page-title">{app.name}</h1>
            <span className="badge accent"><Icon name={FRAMEWORK_ICON[app.framework] ?? 'box'} size={12} />{app.framework} {app.frameworkVersion}</span>
            {app.buildTool && <span className="badge">{app.buildTool}</span>}
            <span className="badge">{app.packageManager}</span>
            {app.typeScript && <span className="badge">TypeScript</span>}
          </div>
          <div className="row small subtle mt-8" style={{ flexWrap: 'nowrap' }}>
            <Icon name="folder" size={14} /><span className="truncate" style={{ minWidth: 0 }}>{app.directory}</span>
            <span className="row gap-4" style={{ flexShrink: 0 }}>
              <button className="btn ghost sm" onClick={() => api.reveal(app.directory).catch(e => toast.error(errorText(e)))}><Icon name="folderOpen" size={14} />Show in folder</button>
              <button className="btn ghost sm" onClick={() => api.openEditor(app.directory).catch(e => toast.error(errorText(e)))}><Icon name="code" size={14} />Open in VS Code</button>
            </span>
          </div>
        </div>
        <div className="row wrap">
          <Button icon="checkCircle" onClick={() => setHealthOpen(true)}>Health</Button>
          <Button icon="history" onClick={() => setHistoryOpen(true)}>History{app.history.length ? ` (${app.history.length})` : ''}</Button>
          <Button icon="download" onClick={() => api.frontendInstall(path).then(r => setJob(r.jobId)).catch(e => toast.error(errorText(e)))}>Install packages</Button>
          <Button variant="primary" icon="hammer" onClick={() => api.frontendBuild(path).then(r => setJob(r.jobId)).catch(e => toast.error(errorText(e)))}>Build</Button>
        </div>
      </div>

      {app.supportNote && <div style={{ marginBottom: 14 }}><Alert kind="info">{app.supportNote}</Alert></div>}
      {app.warnings.map(w => <div key={w} style={{ marginBottom: 14 }}><Alert kind="warning">{w}</Alert></div>)}
      {job && <div className="card pad" style={{ marginBottom: 16 }}><JobView key={job} jobId={job} onDone={j => { if (j.kind === 'install' && j.state === 'succeeded') load() }} /></div>}

      {/* Dev server */}
      <div className="card pad" style={{ marginBottom: 16 }}>
        <div className="row wrap">
          <span className={`status-dot ${run?.state ?? 'stopped'}`} />
          <div className="grow">
            <div className="strong">{run?.state === 'running' ? 'The app is running' : run?.state === 'starting' ? 'Starting the app…' : run?.state === 'failed' ? 'The app couldn’t start' : 'The app is not running'}</div>
            <div className="small subtle">{run?.state === 'running' && run.url ? <>Open it at <a href={run.url} target="_blank" rel="noreferrer" className="mono">{run.url}</a></> : app.devScript ? `Runs “${app.packageManager} run ${app.devScript}” with live reload.` : 'This app has no start/dev script.'}</div>
          </div>
          <RunLog run={run} />
          <Button size="sm" variant={run?.running ? 'default' : 'success'} icon={run?.running ? 'stop' : 'play'} loading={busy || run?.state === 'starting'} disabled={!app.devScript || (!app.installed && !run?.running)} title={!app.installed ? 'Install the packages first' : undefined} onClick={toggleRun}>{run?.running ? 'Stop' : 'Run'}</Button>
        </div>
      </div>

      {/* Connection to the API */}
      <div className="card pad" style={{ marginBottom: 16 }}>
        {app.linkedBackend ? (
          <div className="row wrap">
            <div className="group-icon"><Icon name="server" size={16} /></div>
            <div className="grow">
              <div className="strong">Talks to {app.linkedBackendName}</div>
              <div className="small subtle">{counts.matched} of {app.apiCalls.length} calls reach a real endpoint{counts.problems > 0 ? `, ${counts.problems} need attention` : ''} · {app.unusedEndpoints.length} endpoints aren't used by this app</div>
            </div>
            <Button size="sm" icon="arrowRight" onClick={() => navigate({ page: 'project', path: app.linkedBackend! })}>Open API project</Button>
            <Button size="sm" variant="ghost" onClick={() => setPicker(true)}>Change</Button>
            <Button size="sm" variant="ghost" onClick={() => link(null)}>Disconnect</Button>
          </div>
        ) : (
          <div className="col gap-6">
            <div className="row wrap">
              <div className="group-icon"><Icon name="server" size={16} /></div>
              <div className="grow">
                <div className="strong">Connect this app to its .NET API</div>
                <div className="small subtle">Stackwright then checks that every one of the {app.apiCalls.length} API calls reaches a real endpoint.</div>
              </div>
              <Button size="sm" icon="folderOpen" onClick={() => setPicker(true)}>Choose API project…</Button>
            </div>
            {app.suggestedBackends.length > 0 && (
              <div className="row wrap gap-6">
                <span className="tiny subtle">Nearby:</span>
                {app.suggestedBackends.map(b => <Button key={b.path} size="sm" onClick={() => link(b.path)} title={b.path}>{b.name}{b.framework ? ` · ${b.framework}` : ''}</Button>)}
              </div>
            )}
          </div>
        )}
      </div>

      <div className="stats">
        <div className="card stat"><div className="stat-value">{pages}</div><div className="stat-label">Pages</div></div>
        <div className="card stat"><div className="stat-value">{app.components.filter(c => c.kind === 'component' || c.kind === 'page').length}</div><div className="stat-label">Components</div></div>
        <div className="card stat"><div className="stat-value">{app.apiCalls.length}</div><div className="stat-label">API calls</div></div>
        <div className="card stat"><div className="stat-value" style={{ color: counts.problems ? 'var(--danger)' : undefined }}>{app.linkedBackend ? counts.problems : '—'}</div><div className="stat-label">Calls that need attention</div></div>
      </div>

      <div className="tabs" style={{ marginTop: 0, marginBottom: 18 }}>
        {([['calls', 'send', 'API calls', app.apiCalls.length], ['pages', 'layers', 'Pages & routes', app.routes.length], ['components', 'box', 'Components', app.components.length], ['settings', 'key', 'Settings', null], ['deploy', 'rocket', 'Publish & deploy', null], ['git', 'history', 'Source control', null]] as const).map(([id, icon, label, n]) => (
          <button key={id} className={`tab${tab === id ? ' active' : ''}`} onClick={() => setTab(id)}>
            <span className="row gap-6"><Icon name={icon} size={15} />{label}{n != null && <span className="badge" style={{ height: 18 }}>{n}</span>}</span>
          </button>
        ))}
      </div>

      {tab === 'calls' && <CallsTab app={app} />}
      {tab === 'pages' && <PagesTab app={app} />}
      {tab === 'components' && <ComponentsTab app={app} />}
      {tab === 'settings' && <SettingsPanel path={path} onChanged={load} frontend />}
      {tab === 'git' && <GitPanel path={path} />}
      {tab === 'deploy' && <DeployPanel project={{ path: app.path, name: app.name, platform: 'Frontend', frontend: true }} />}

      {picker && <FolderPicker mode="project" onPick={p => { if (p.toLowerCase().endsWith('.csproj')) link(p); else toast.error('Pick the .NET API project (.csproj).') }} onClose={() => setPicker(false)} />}
      {healthOpen && <HealthDialog frontend path={path} onClose={() => setHealthOpen(false)} onOpenEndpoint={() => {}} onUpgrade={() => {}} />}
      {historyOpen && <HistoryDialog history={app.history} onClose={() => setHistoryOpen(false)} onUndo={async () => {
        try { const r = await api.frontendUndo(path); setApp(r.project); toast.info(r.message) } catch (e) { toast.error(errorText(e)) }
      }} />}
    </div>
  )
}

function RunLog({ run }: { run: RunStatus | null }) {
  const [open, setOpen] = useState(false)
  if (!run || run.log.length < 2) return null
  return (
    <>
      <Button size="sm" variant="ghost" icon="fileText" onClick={() => setOpen(o => !o)}>{open ? 'Hide output' : 'Show output'}</Button>
      {open && <div className="log" style={{ flexBasis: '100%', marginTop: 12 }}>{run.log.join('\n')}</div>}
    </>
  )
}

function CallsTab({ app }: { app: FrontendDetails }) {
  const [filter, setFilter] = useState<'all' | FrontendApiCall['match']>(app.linkedBackend && app.apiCalls.some(c => c.match === 'missing' || c.match === 'method') ? 'missing' : 'all')
  const [search, setSearch] = useState('')
  const [showUnused, setShowUnused] = useState(false)
  const list = useMemo(() => app.apiCalls.filter(c =>
    (filter === 'all' || c.match === filter || (filter === 'missing' && c.match === 'method')) &&
    (`${c.method} ${c.url} ${c.file} ${c.caller ?? ''}`.toLowerCase().includes(search.toLowerCase()))), [app, filter, search])
  const by = (m: string) => app.apiCalls.filter(c => c.match === m).length

  if (app.apiCalls.length === 0) return <div className="card"><Empty icon="send" title="No API calls found">This app doesn't call any API with HttpClient, fetch or axios.</Empty></div>
  return (
    <div className="col gap-16">
      <div className="toolbar" style={{ marginBottom: 0 }}>
        <div className="search"><Icon name="search" size={16} /><input className="input" placeholder="Search calls, files, functions…" value={search} onChange={e => setSearch(e.target.value)} /></div>
        <div className="segmented">
          <button className={filter === 'all' ? 'active' : ''} onClick={() => setFilter('all')}>All <span className="subtle">{app.apiCalls.length}</span></button>
          {app.linkedBackend && <button className={filter === 'missing' ? 'active' : ''} onClick={() => setFilter('missing')}><span style={{ color: 'var(--danger)' }}>Problems</span> <span className="subtle">{by('missing') + by('method')}</span></button>}
          {app.linkedBackend && <button className={filter === 'matched' ? 'active' : ''} onClick={() => setFilter('matched')}><span style={{ color: 'var(--success)' }}>Found</span> <span className="subtle">{by('matched')}</span></button>}
          <button className={filter === 'unknown' ? 'active' : ''} onClick={() => setFilter('unknown')}>Can't check <span className="subtle">{by('unknown')}</span></button>
          <button className={filter === 'asset' ? 'active' : ''} onClick={() => setFilter('asset')}>App files <span className="subtle">{by('asset')}</span></button>
        </div>
      </div>
      <div className="card">
        {list.length === 0 && <Empty icon="checkCircle" title="Nothing here">{filter === 'missing' ? 'Every call reaches a real endpoint.' : 'No calls match.'}</Empty>}
        {list.slice(0, 400).map(c => {
          const m = MATCH[c.match]
          return (
            <div key={c.id} className="endpoint" style={{ cursor: 'default' }}>
              <MethodBadge method={c.method} />
              <div style={{ minWidth: 0 }}>
                <div className="mono small" style={{ wordBreak: 'break-all' }}>{c.url || '(address built at run time)'}</div>
                <div className="meta">
                  <span className="tiny subtle">{c.file}:{c.line}{c.caller ? ` · in ${c.caller}()` : ''}</span>
                  {c.endpointRoute && (
                    <span className="chip" style={{ color: c.match === 'matched' ? 'var(--success)' : 'var(--warning)' }}>
                      <Icon name="arrowRight" size={11} />{c.endpointMethod} {c.endpointRoute}
                    </span>
                  )}
                </div>
              </div>
              <div className="row gap-4">
                <span className={`badge ${m.cls}`} title={m.hint}>{m.label}</span>
                {c.endpointId && app.linkedBackend && <Button size="sm" variant="ghost" iconOnly icon="eye" title="Open the endpoint" onClick={() => { window.location.hash = href({ page: 'project', path: app.linkedBackend! }) + `&endpoint=${encodeURIComponent(c.endpointId!)}` }} />}
                <Button size="sm" variant="ghost" iconOnly icon="code" title="Open in VS Code" onClick={() => api.openEditor(joinPath(app.directory, c.file), c.line)} />
              </div>
            </div>
          )
        })}
      </div>
      {app.linkedBackend && app.unusedEndpoints.length > 0 && (
        <div className="card">
          <div className="group-header" onClick={() => setShowUnused(s => !s)}>
            <Icon name={showUnused ? 'chevronDown' : 'chevronRight'} size={16} />
            <div className="grow"><div className="group-title">{app.unusedEndpoints.length} endpoints this app never calls</div><div className="tiny subtle">They may be used by other apps, or no longer be needed.</div></div>
          </div>
          {showUnused && app.unusedEndpoints.map(e => (
            <div key={e.id} className="endpoint" onClick={() => { window.location.hash = href({ page: 'project', path: app.linkedBackend! }) + `&endpoint=${encodeURIComponent(e.id)}` }}>
              <MethodBadge method={e.method} /><span className="route">{e.route}</span><span className="small subtle">{e.name}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function PagesTab({ app }: { app: FrontendDetails }) {
  const [search, setSearch] = useState('')
  const list = app.routes.filter(r => `${r.path} ${r.component ?? ''}`.toLowerCase().includes(search.toLowerCase()))
  if (app.routes.length === 0) return <div className="card"><Empty icon="layers" title="No routes found">{app.framework === 'React' ? 'No React Router routes were found (pages may be switched another way).' : 'No route definitions were found.'}</Empty></div>
  return (
    <div className="col gap-16">
      <div className="search"><Icon name="search" size={16} /><input className="input" placeholder="Search pages…" value={search} onChange={e => setSearch(e.target.value)} /></div>
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>Address</th><th>Shows</th><th>Type</th><th>Protected by</th><th>Defined in</th></tr></thead>
          <tbody>
            {list.map((r, i) => (
              <tr key={i}>
                <td className="mono small" style={{ wordBreak: 'break-all' }}>{r.path.replace(/:(\w+)/g, '{$1}')}</td>
                <td className="small">{r.component ?? <span className="subtle">—</span>}</td>
                <td><span className={`badge ${r.kind === 'redirect' ? '' : r.kind === 'api' ? 'warning' : 'accent'}`}>{r.kind === 'section' ? 'Section (loads more pages)' : r.kind === 'lazy page' ? 'Page (loaded on demand)' : r.kind === 'redirect' ? 'Redirect' : r.kind === 'api' ? 'Server API' : 'Page'}</span></td>
                <td>{r.guards.length ? r.guards.map(g => <span key={g} className="chip" style={{ marginRight: 4 }}><Icon name="lock" size={11} />{g}</span>) : <span className="subtle small">—</span>}</td>
                <td className="tiny subtle">{r.file}:{r.line}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function ComponentsTab({ app }: { app: FrontendDetails }) {
  const [search, setSearch] = useState('')
  const kinds: Record<string, string> = { page: 'Pages', component: 'Components', service: 'Services', hook: 'Hooks', directive: 'Directives', pipe: 'Pipes', module: 'Modules' }
  const groups = Object.keys(kinds).map(k => [k, app.components.filter(c => c.kind === k && `${c.name} ${c.selector ?? ''} ${c.file}`.toLowerCase().includes(search.toLowerCase()))] as const).filter(([, l]) => l.length > 0)
  return (
    <div className="col gap-16">
      <div className="search"><Icon name="search" size={16} /><input className="input" placeholder="Search components, services…" value={search} onChange={e => setSearch(e.target.value)} /></div>
      {groups.length === 0 && <div className="card"><Empty icon="box" title="Nothing found" /></div>}
      {groups.map(([kind, list]) => (
        <div key={kind} className="card">
          <div className="group-header" style={{ cursor: 'default' }}><div className="group-title">{kinds[kind]}</div><span className="tiny subtle">{list.length}</span></div>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
            {list.map(c => (
              <div key={c.file + c.name} className="endpoint" style={{ gridTemplateColumns: 'minmax(0,1fr) auto', borderRight: '1px solid var(--border)' }} onClick={() => api.openEditor(joinPath(app.directory, c.file), c.line)} title="Open in VS Code">
                <div style={{ minWidth: 0 }}>
                  <div className="strong small truncate">{c.name}</div>
                  <div className="tiny subtle truncate">{c.selector ? <span className="mono">&lt;{c.selector}&gt; · </span> : null}{c.file}</div>
                </div>
                <Icon name="code" size={14} />
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

function joinPath(dir: string, rel: string) {
  const sep = dir.includes('\\') ? '\\' : '/'
  return dir.replace(/[\\/]+$/, '') + sep + rel.replace(/\//g, sep)
}
