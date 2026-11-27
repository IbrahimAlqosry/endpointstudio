import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { BuildDialog } from '../components/BuildDialog'
import { EndpointDrawer } from '../components/EndpointDrawer'
import { EndpointEditor } from '../components/EndpointEditor'
import { GitPanel } from '../components/GitPanel'
import { usePaletteItems, type PaletteItem } from '../palette'
import { DeployPanel } from '../components/DeployPanel'
import { HealthDialog } from '../components/HealthDialog'
import { SettingsPanel } from '../components/SettingsPanel'
import { HistoryDialog } from '../components/HistoryDialog'
import { UpgradeDialog } from '../components/UpgradeDialog'
import { Icon } from '../components/Icon'
import { Alert, Button, Empty, MethodBadge, RouteText, Spinner, errorText, useConfirm, useToast } from '../components/ui'
import { href } from '../router'
import type { BuildResult, EndpointInfo, ProjectDetails, RunStatus } from '../types'

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'] as const
const METHOD_COLORS: Record<string, string> = { GET: 'var(--get)', POST: 'var(--post)', PUT: 'var(--put)', PATCH: 'var(--patch)', DELETE: 'var(--delete)' }

export function ProjectPage({ path, onChanged }: { path: string; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [project, setProject] = useState<ProjectDetails | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [methodFilter, setMethodFilter] = useState<string | null>(null)
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<string | null>(() => new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('endpoint'))
  const [drawerTab, setDrawerTab] = useState<'overview' | 'try'>('overview')
  const [editor, setEditor] = useState<{ mode: 'add'; groupKey?: string; templateId?: string } | { mode: 'edit'; endpoint: EndpointInfo } | null>(null)
  const [build, setBuild] = useState<BuildResult | 'loading' | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [healthOpen, setHealthOpen] = useState(false)
  const [view, setView] = useState<'endpoints' | 'settings' | 'deploy' | 'git'>(() => (new URLSearchParams(window.location.hash.split('?')[1] ?? '').get('tab') as 'settings' | 'deploy' | 'git') || 'endpoints')
  const [upgradeOpen, setUpgradeOpen] = useState(false)
  const [run, setRun] = useState<RunStatus | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setProject(await api.project(path))
      setLoadError(null)
    } catch (e) { setLoadError(errorText(e)) }
  }, [path])

  useEffect(() => { load() }, [load])

  // Poll the running app status
  useEffect(() => {
    let alive = true
    let timer: number
    const tick = async () => {
      try {
        const s = await api.runStatus(path)
        if (!alive) return
        setRun(s)
        timer = window.setTimeout(tick, s.state === 'starting' || s.state === 'stopping' ? 1000 : 4000)
      } catch { timer = window.setTimeout(tick, 8000) }
    }
    tick()
    return () => { alive = false; clearTimeout(timer) }
  }, [path])

  const applyChange = (p: ProjectDetails, message: string) => {
    setProject(p)
    onChanged()
    toast.success(message, p.history.length > 0 ? { label: 'Undo', onClick: () => undo() } : undefined)
  }

  const undo = async () => {
    try {
      const r = await api.undo(path)
      setProject(r.project)
      onChanged()
      toast.info(r.message)
    } catch (e) { toast.error(errorText(e)) }
  }

  const remove = async (ep: EndpointInfo) => {
    const ok = await confirm({
      title: 'Delete this endpoint?',
      message: <>The endpoint <strong>{ep.httpMethod} {ep.route}</strong> will be removed from the project. You can undo this afterwards.</>,
      confirmText: 'Delete endpoint',
      danger: true,
    })
    if (!ok) return
    try {
      const r = await api.deleteEndpoint(path, ep.id)
      if (selected === ep.id) setSelected(null)
      applyChange(r.project, r.message)
    } catch (e) { toast.error(errorText(e)) }
  }

  const runBuild = async () => {
    setBuild('loading')
    try { setBuild(await api.build(path)) } catch (e) { setBuild(null); toast.error(errorText(e)) }
  }

  const toggleRun = async () => {
    setBusy(true)
    try {
      if (run?.running) { setRun(await api.stop(path)); toast.info('The app was stopped.') }
      else { setRun(await api.run(path)); toast.info('Starting your app… this can take a few seconds.') }
    } catch (e) { toast.error(errorText(e)) } finally { setBusy(false) }
  }

  const exportPostman = (kind: 'postman' | 'openapi') => {
    const a = document.createElement('a')
    a.href = kind === 'postman' ? api.postmanUrl(path, run?.running ? run.url : null) : api.openApiUrl(path, run?.running ? run.url : null)
    a.download = ''
    document.body.appendChild(a); a.click(); a.remove()
    toast.success(kind === 'postman'
      ? 'Postman collection downloaded. Import it into Postman to share or test your API.'
      : 'OpenAPI document downloaded. Use it with Swagger, API gateways, client generators or Postman.')
  }

  const filtered = useMemo(() => {
    if (!project) return []
    const s = search.trim().toLowerCase()
    return project.endpoints.filter(e =>
      (!methodFilter || e.httpMethod === methodFilter) &&
      (!s || e.route.toLowerCase().includes(s) || e.name.toLowerCase().includes(s) || (e.summary ?? '').toLowerCase().includes(s)))
  }, [project, search, methodFilter])

  const paletteItems = useMemo<PaletteItem[] | null>(() => {
    if (!project) return null
    const tab = (v: 'endpoints' | 'settings' | 'deploy' | 'git', title: string, icon: string, keywords = '') =>
      ({ id: 'tab-' + v, group: project.name, title, icon, keywords, run: () => setView(v) })
    return [
      tab('endpoints', 'Endpoints', 'layers'),
      tab('settings', 'Settings', 'key', 'appsettings config connection strings'),
      tab('deploy', 'Publish & deploy', 'rocket', 'docker iis azure ftp ssh zip'),
      tab('git', 'Source control', 'history', 'git commit branch push pull'),
      { id: 'act-health', group: project.name, title: 'Health check', icon: 'checkCircle', keywords: 'security problems', run: () => setHealthOpen(true) },
      ...project.endpoints.map(e => ({
        id: 'ep-' + e.id, group: 'Endpoints', title: `${e.httpMethod} ${e.route}`, subtitle: e.summary ?? e.name, icon: 'send', keywords: e.name,
        run: () => { setView('endpoints'); setSelected(e.id); setDrawerTab('overview') },
      })),
    ]
  }, [project])
  usePaletteItems('project', paletteItems)

  if (loadError) {
    return (
      <div className="page">
        <div className="card">
          <Empty icon="alert" title="This project couldn't be opened" action={<a className="btn" href={href({ page: 'home' })}>Back to home</a>}>{loadError}</Empty>
        </div>
      </div>
    )
  }
  if (!project) return <div className="page"><div className="empty"><Spinner large /><span className="muted">Reading your project…</span></div></div>

  const counts = Object.fromEntries(METHODS.map(m => [m, project.endpoints.filter(e => e.httpMethod === m).length]))
  const secured = project.endpoints.filter(e => e.requiresAuth).length
  const selectedEp = project.endpoints.find(e => e.id === selected) ?? null
  const groups = project.groups
    .map(g => ({ group: g, endpoints: filtered.filter(e => e.groupKey === g.key) }))
    .filter(x => x.endpoints.length > 0 || (!search && !methodFilter))
    .sort((a, b) => a.group.name.localeCompare(b.group.name))

  return (
    <div className="page">
      <div className="page-header">
        <div className="grow" style={{ minWidth: 260 }}>
          <div className="crumbs">
            <a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} />
            {project.solutionPath && <><a href={href({ page: 'solution', path: project.solutionPath })}>{fileName(project.solutionPath)}</a><Icon name="chevronRight" size={12} /></>}
            <span>{project.name}</span>
          </div>
          <div className="row wrap">
            <h1 className="page-title">{project.name}</h1>
            <span className={`badge${project.capabilities.platform === 'NetFramework' ? ' warning' : ''}`} title={project.targetFramework}>{project.capabilities.frameworkLabel}</span>
            <span className="badge accent">{project.capabilities.platform === 'NetFramework' ? 'Web API 2' : project.preferredStyle === 'Controller' ? 'Controllers' : 'Minimal APIs'}</span>
            {project.entities.length > 0 && <span className="badge success"><Icon name="database" size={12} /> Database connected</span>}
          </div>
          <div className="row small subtle mt-8" title={project.directory} style={{ flexWrap: 'nowrap' }}>
            <Icon name="folder" size={14} /><span className="truncate" style={{ minWidth: 0, flexShrink: 1 }}>{project.directory}</span>
            <span className="row gap-4" style={{ flexShrink: 0 }}>
              <button className="btn ghost sm" onClick={() => api.reveal(project.path).catch(e => toast.error(errorText(e)))}><Icon name="folderOpen" size={14} />Show in folder</button>
              <button className="btn ghost sm" onClick={() => api.openEditor(project.path).catch(e => toast.error(errorText(e)))}><Icon name="code" size={14} />Open in VS Code</button>
            </span>
          </div>
        </div>
        <div className="row wrap">
          <Button icon="checkCircle" onClick={() => setHealthOpen(true)}>Health</Button>
          {project.capabilities.supportNote && <Button icon="rocket" onClick={() => setUpgradeOpen(true)}>Upgrade</Button>}
          <Button icon="history" onClick={() => setHistoryOpen(true)}>History{project.history.length > 0 ? ` (${project.history.length})` : ''}</Button>
          <Button icon="hammer" onClick={runBuild} disabled={run?.running || !project.capabilities.canBuild} title={project.capabilities.buildNote ?? undefined}>Check build</Button>
          <Button variant="primary" icon="plus" onClick={() => setEditor({ mode: 'add' })}>Add endpoint</Button>
        </div>
      </div>

      {project.warnings.map(w => <div key={w} style={{ marginBottom: 14 }}><Alert kind="warning">{w}</Alert></div>)}
      {project.capabilities.supportNote && (
        <div style={{ marginBottom: 14 }}>
          <Alert kind="info">
            <div className="row wrap" style={{ justifyContent: 'space-between' }}>
              <span>{project.capabilities.supportNote}</span>
              {project.capabilities.platform !== 'NetFramework' && <Button size="sm" icon="rocket" onClick={() => setUpgradeOpen(true)}>Upgrade assistant</Button>}
            </div>
          </Alert>
        </div>
      )}
      {project.capabilities.buildNote && <div style={{ marginBottom: 14 }}><Alert kind="warning">{project.capabilities.buildNote}</Alert></div>}

      <div className="tabs" style={{ marginTop: 0, marginBottom: 18 }}>
        <button className={`tab${view === 'endpoints' ? ' active' : ''}`} onClick={() => setView('endpoints')}><span className="row gap-6"><Icon name="layers" size={15} />Endpoints <span className="badge" style={{ height: 18 }}>{project.endpoints.length}</span></span></button>
        <button className={`tab${view === 'settings' ? ' active' : ''}`} onClick={() => setView('settings')}><span className="row gap-6"><Icon name="key" size={15} />Settings</span></button>
        <button className={`tab${view === 'deploy' ? ' active' : ''}`} onClick={() => setView('deploy')}><span className="row gap-6"><Icon name="rocket" size={15} />Publish &amp; deploy</span></button>
        <button className={`tab${view === 'git' ? ' active' : ''}`} onClick={() => setView('git')}><span className="row gap-6"><Icon name="history" size={15} />Source control</span></button>
      </div>

      {view === 'settings' && <SettingsPanel path={path} onChanged={load} />}
      {view === 'git' && <GitPanel path={path} />}
      {view === 'deploy' && <DeployPanel project={{ path: project.path, name: project.name, platform: project.capabilities.platform }} />}
      {view === 'endpoints' && <>
      <RunBar run={run} busy={busy} onToggle={toggleRun} onExport={exportPostman} canRun={project.capabilities.canRun} runNote={project.capabilities.runNote} framework={project.capabilities.platform === 'NetFramework'} />

      <SolutionStructure project={project} />

      <div className="stats">
        <div className="card stat">
          <div className="stat-value">{project.endpoints.length}</div>
          <div className="stat-label">Endpoints</div>
          <div className="method-bar">
            {METHODS.map(m => counts[m] > 0 && <span key={m} style={{ width: `${(counts[m] / project.endpoints.length) * 100}%`, background: METHOD_COLORS[m] }} title={`${m}: ${counts[m]}`} />)}
          </div>
        </div>
        <div className="card stat"><div className="stat-value">{project.groups.length}</div><div className="stat-label">Groups</div></div>
        <div className="card stat"><div className="stat-value">{project.entities.length}</div><div className="stat-label">Data tables</div></div>
        <div className="card stat"><div className="stat-value">{secured}</div><div className="stat-label">Require sign-in</div></div>
      </div>

      <div className="toolbar">
        <div className="search">
          <Icon name="search" size={16} />
          <input className="input" placeholder="Search by path, name or description…" value={search} onChange={e => setSearch(e.target.value)} />
        </div>
        <div className="segmented">
          <button className={!methodFilter ? 'active' : ''} onClick={() => setMethodFilter(null)}>All</button>
          {METHODS.filter(m => counts[m] > 0).map(m => (
            <button key={m} className={methodFilter === m ? 'active' : ''} onClick={() => setMethodFilter(methodFilter === m ? null : m)}>
              <span style={{ color: METHOD_COLORS[m] }}>{m}</span> <span className="subtle">{counts[m]}</span>
            </button>
          ))}
        </div>
      </div>

      {project.endpoints.length === 0 ? (
        <div className="card">
          <Empty icon="layers" title="No endpoints yet" action={<Button variant="primary" icon="plus" onClick={() => setEditor({ mode: 'add' })}>Add your first endpoint</Button>}>
            This project doesn’t have any endpoints. Add one and QosriStudio will write it into the project for you.
          </Empty>
        </div>
      ) : groups.length === 0 ? (
        <div className="card"><Empty icon="search" title="Nothing matches your search">Try a different word or clear the filter.</Empty></div>
      ) : groups.map(({ group, endpoints }) => {
        const isCollapsed = collapsed.has(group.key)
        return (
          <div key={group.key} className={`card group${isCollapsed ? ' collapsed' : ''}`}>
            <div className="group-header" onClick={() => setCollapsed(c => { const n = new Set(c); if (n.has(group.key)) n.delete(group.key); else n.add(group.key); return n })}>
              <Icon name={isCollapsed ? 'chevronRight' : 'chevronDown'} size={16} />
              <div className="group-icon"><Icon name={group.style === 'Controller' ? 'layers' : 'zap'} size={16} /></div>
              <div className="grow">
                <div className="row gap-6">
                  <span className="group-title">{group.name}</span>
                  {endpoints[0] && endpoints[0].project !== project.name && <span className="badge" title="These endpoints come from a library project used by this API">from {endpoints[0].project}</span>}
                </div>
                <div className="tiny subtle"><span className="mono">{group.baseRoute}</span> · {endpoints.length} endpoint{endpoints.length === 1 ? '' : 's'}</div>
              </div>
              <Button size="sm" variant="ghost" icon="plus" onClick={e => { e.stopPropagation(); setEditor({ mode: 'add', groupKey: group.key }) }}>Add here</Button>
            </div>
            {!isCollapsed && endpoints.map(ep => (
              <div key={ep.id} className={`endpoint${selected === ep.id ? ' selected' : ''}`} onClick={() => { setSelected(ep.id); setDrawerTab('overview') }}>
                <MethodBadge method={ep.httpMethod} />
                <div style={{ minWidth: 0 }}>
                  <div className="row gap-6">
                    <RouteText route={ep.route} />
                    {ep.requiresAuth
                      ? <span className="badge warning" style={{ height: 20 }} title="Requires sign-in"><Icon name="lock" size={11} />Sign-in</span>
                      : <span className="badge" style={{ height: 20 }} title="Anyone can call it"><Icon name="unlock" size={11} />Public</span>}
                    {ep.project !== project.name && <span className="badge" style={{ height: 20 }}>{ep.project}</span>}
                  </div>
                  <div className="meta">
                    {ep.name && <span className="small strong muted">{splitWords(ep.name)}</span>}
                    {ep.summary && <span className="small subtle truncate" style={{ maxWidth: 380 }}>— {ep.summary}</span>}
                  </div>
                  <div className="meta">
                    {ep.parameters.filter(p => p.source !== 'Service').map(p => (
                      <span key={p.name} className="chip" title={`${p.type} · ${p.source === 'Route' ? 'in the path' : p.source === 'Query' ? 'in the query string' : p.source === 'Body' ? 'in the body' : 'in a header'}${p.optional ? ' · optional' : ''}`}>
                        <span className="subtle">{p.source === 'Route' ? 'path' : p.source === 'Query' ? 'query' : p.source === 'Body' ? 'body' : 'header'}</span> {p.name}{p.optional ? '?' : ''}
                      </span>
                    ))}
                    {ep.calls && <span className="chip" style={{ color: 'var(--accent-text)' }} title="Service operation it calls"><Icon name="zap" size={11} />{splitWords(ep.calls.split('.')[1])}</span>}
                    <span className="chip" title="What it returns"><Icon name="arrowLeft" size={11} />{ep.returns}</span>
                  </div>
                </div>
                <div className="actions">
                  <Button size="sm" variant="ghost" iconOnly icon="send" title="Try it" onClick={e => { e.stopPropagation(); setSelected(ep.id); setDrawerTab('try') }} />
                  <Button size="sm" variant="ghost" iconOnly icon="copy" title="Make a copy" onClick={e => { e.stopPropagation(); setEditor({ mode: 'add', templateId: ep.id }) }} />
                  <Button size="sm" variant="ghost" iconOnly icon="edit" title="Edit" onClick={e => { e.stopPropagation(); setEditor({ mode: 'edit', endpoint: ep }) }} />
                  <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete" onClick={e => { e.stopPropagation(); remove(ep) }} />
                </div>
              </div>
            ))}
            {!isCollapsed && endpoints.length === 0 && <div className="endpoint subtle small" style={{ cursor: 'default' }}>No endpoints in this group yet.</div>}
          </div>
        )
      })}

      </>}

      {selectedEp && (
        <EndpointDrawer
          key={selectedEp.id}
          project={project}
          endpoint={selectedEp}
          run={run}
          tab={drawerTab}
          onTab={setDrawerTab}
          onClose={() => setSelected(null)}
          onEdit={() => setEditor({ mode: 'edit', endpoint: selectedEp })}
          onDelete={() => remove(selectedEp)}
          onCopy={() => setEditor({ mode: 'add', templateId: selectedEp.id })}
          onStart={toggleRun}
        />
      )}

      {editor && (
        <EndpointEditor
          project={project}
          initial={editor}
          onClose={() => setEditor(null)}
          onSaved={(p, msg) => { setEditor(null); applyChange(p, msg) }}
        />
      )}

      {build && <BuildDialog result={build} onClose={() => setBuild(null)} onRetry={runBuild} />}
      {healthOpen && <HealthDialog path={path} onClose={() => setHealthOpen(false)} onOpenEndpoint={id => { setSelected(id); setDrawerTab('overview') }} onUpgrade={() => setUpgradeOpen(true)} />}
      {upgradeOpen && <UpgradeDialog project={project} onClose={() => setUpgradeOpen(false)} onDone={(p, msg) => applyChange(p, msg)} />}
      {historyOpen && <HistoryDialog history={project.history} onClose={() => setHistoryOpen(false)} onUndo={async () => { await undo(); }} />}
    </div>
  )
}

function SolutionStructure({ project }: { project: ProjectDetails }) {
  const [open, setOpen] = useState(false)
  const refs = project.referencedProjects
  if (refs.length === 0 && project.binaryReferences.length === 0 && project.services.length === 0) return null
  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <div className="group-header" onClick={() => setOpen(o => !o)} style={{ borderBottom: open ? undefined : 'none' }}>
        <Icon name={open ? 'chevronDown' : 'chevronRight'} size={16} />
        <div className="group-icon"><Icon name="layers" size={16} /></div>
        <div className="grow">
          <div className="group-title">How this API is built</div>
          <div className="tiny subtle">
            Uses {refs.length} project{refs.length === 1 ? '' : 's'}
            {project.services.length > 0 && <> · {project.services.length} service{project.services.length === 1 ? '' : 's'}</>}
            {project.binaryReferences.length > 0 && <> · {project.binaryReferences.length} shared DLL{project.binaryReferences.length === 1 ? '' : 's'}</>}
          </div>
        </div>
        <div className="row wrap gap-4">
          {refs.slice(0, 4).map(r => <span key={r.path} className="chip">{r.role}: {r.name}</span>)}
        </div>
      </div>
      {open && (
        <div className="col gap-16" style={{ padding: 16 }}>
          {refs.length > 0 && (
            <div>
              <div className="label" style={{ marginBottom: 8 }}>Projects it uses</div>
              <div className="row wrap gap-6">
                {refs.map(r => (
                  <a key={r.path} className="chip" href={href({ page: 'project', path: r.path })} title={r.path} style={{ height: 26 }}>
                    <Icon name={r.role === 'Services' ? 'zap' : r.role === 'Models' ? 'box' : r.role === 'Data access' ? 'database' : 'file'} size={12} />
                    {r.name} <span className="subtle">· {r.role}{r.framework ? ` · ${r.framework}` : ''}</span>
                  </a>
                ))}
              </div>
            </div>
          )}
          {project.services.length > 0 && (
            <div>
              <div className="label" style={{ marginBottom: 8 }}>Services the endpoints call</div>
              <div className="table-wrap">
                <table className="table">
                  <thead><tr><th>Service</th><th>Implemented by</th><th>Project</th><th>Operations</th></tr></thead>
                  <tbody>
                    {project.services.map(s => (
                      <tr key={s.interface + s.project}>
                        <td className="strong">{splitWords(s.interface.replace(/^I(?=[A-Z])/, ''))}</td>
                        <td>{s.implementation ? splitWords(s.implementation) : <span className="subtle">Not found</span>}</td>
                        <td className="small muted">{s.project}</td>
                        <td>{s.methods}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
          {project.binaryReferences.length > 0 && (
            <div>
              <div className="label" style={{ marginBottom: 8 }}>Shared libraries (prebuilt DLLs)</div>
              <div className="row wrap gap-6">{project.binaryReferences.map(b => <span key={b} className="chip">{b}</span>)}</div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function fileName(p: string) {
  return (p.split(/[\\/]/).pop() ?? p).replace(/\.slnx?$/i, '')
}

function RunBar({ run, busy, onToggle, onExport, canRun, runNote, framework }: {
  run: RunStatus | null; busy: boolean; onToggle: () => void; onExport: (kind: 'postman' | 'openapi') => void; canRun: boolean; runNote: string | null; framework: boolean
}) {
  const [showLog, setShowLog] = useState(false)
  const state = run?.state ?? 'stopped'
  const label = state === 'running' ? 'Your API is running' : state === 'starting' ? 'Starting your API…' : state === 'failed' ? 'Your API couldn’t start' : 'Your API is not running'
  return (
    <div className="card pad" style={{ marginBottom: 16 }}>
      <div className="row wrap">
        <span className={`status-dot ${state}`} />
        <div className="grow">
          <div className="strong">{label}</div>
          <div className="small subtle">
            {state === 'running' && run?.url ? <>Available at <a href={run.url} target="_blank" rel="noreferrer" className="mono">{run.url}</a> — try any endpoint from its details.</> :
              state === 'failed' ? 'Check the build or the output to see what went wrong.' :
                !canRun ? runNote :
                  framework ? 'Start it with IIS Express to try your endpoints right here.' : 'Start it to try your endpoints right here.'}
          </div>
        </div>
        {run && run.log.length > 1 && <Button size="sm" variant="ghost" icon="fileText" onClick={() => setShowLog(s => !s)}>{showLog ? 'Hide output' : 'Show output'}</Button>}
        <ExportMenu onPick={onExport} />
        <Button size="sm" variant={run?.running ? 'default' : 'success'} icon={run?.running ? 'stop' : 'play'} loading={busy || state === 'starting'} onClick={onToggle} disabled={!run?.running && !canRun} title={!canRun ? runNote ?? undefined : undefined}>
          {run?.running ? 'Stop' : 'Run'}
        </Button>
      </div>
      {showLog && run && <div className="log mt-16">{run.log.join('\n')}</div>}
    </div>
  )
}

function splitWords(s: string) {
  return s.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
}

function ExportMenu({ onPick }: { onPick: (kind: 'postman' | 'openapi') => void }) {
  const [open, setOpen] = useState(false)
  return (
    <div style={{ position: 'relative' }}>
      <Button size="sm" icon="download" onClick={() => setOpen(o => !o)}>Export</Button>
      {open && (
        <>
          <div style={{ position: 'fixed', inset: 0, zIndex: 20 }} onClick={() => setOpen(false)} />
          <div className="menu" style={{ right: 0, width: 260 }}>
            <button className="menu-item" onClick={() => { setOpen(false); onPick('openapi') }}><div className="strong">OpenAPI document</div><div className="tiny subtle">For Swagger, gateways and client generators</div></button>
            <button className="menu-item" onClick={() => { setOpen(false); onPick('postman') }}><div className="strong">Postman collection</div><div className="tiny subtle">Every endpoint with example bodies</div></button>
          </div>
        </>
      )}
    </div>
  )
}
