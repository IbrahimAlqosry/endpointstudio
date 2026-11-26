import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { BuildDialog } from '../components/BuildDialog'
import { EndpointDrawer } from '../components/EndpointDrawer'
import { EndpointEditor } from '../components/EndpointEditor'
import { HistoryDialog } from '../components/HistoryDialog'
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
  const [selected, setSelected] = useState<string | null>(null)
  const [drawerTab, setDrawerTab] = useState<'overview' | 'try'>('overview')
  const [editor, setEditor] = useState<{ mode: 'add'; groupKey?: string } | { mode: 'edit'; endpoint: EndpointInfo } | null>(null)
  const [build, setBuild] = useState<BuildResult | 'loading' | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
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

  const exportPostman = () => {
    const a = document.createElement('a')
    a.href = api.postmanUrl(path, run?.running ? run.url : null)
    a.download = ''
    document.body.appendChild(a); a.click(); a.remove()
    toast.success('Postman collection downloaded. Import it into Postman to share or test your API.')
  }

  const filtered = useMemo(() => {
    if (!project) return []
    const s = search.trim().toLowerCase()
    return project.endpoints.filter(e =>
      (!methodFilter || e.httpMethod === methodFilter) &&
      (!s || e.route.toLowerCase().includes(s) || e.name.toLowerCase().includes(s) || (e.summary ?? '').toLowerCase().includes(s)))
  }, [project, search, methodFilter])

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
          <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>Project</span></div>
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
          <Button icon="history" onClick={() => setHistoryOpen(true)}>History{project.history.length > 0 ? ` (${project.history.length})` : ''}</Button>
          <Button icon="hammer" onClick={runBuild} disabled={run?.running || !project.capabilities.canBuild} title={project.capabilities.buildNote ?? undefined}>Check build</Button>
          <Button variant="primary" icon="plus" onClick={() => setEditor({ mode: 'add' })}>Add endpoint</Button>
        </div>
      </div>

      {project.warnings.map(w => <div key={w} style={{ marginBottom: 14 }}><Alert kind="warning">{w}</Alert></div>)}
      {project.capabilities.supportNote && <div style={{ marginBottom: 14 }}><Alert kind="info">{project.capabilities.supportNote}</Alert></div>}
      {project.capabilities.buildNote && <div style={{ marginBottom: 14 }}><Alert kind="warning">{project.capabilities.buildNote}</Alert></div>}

      <RunBar run={run} busy={busy} onToggle={toggleRun} onExport={exportPostman} canRun={project.capabilities.canRun} runNote={project.capabilities.runNote} framework={project.capabilities.platform === 'NetFramework'} />

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
            This project doesn’t have any endpoints. Add one and Endpoint Studio will write it into the project for you.
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
                <div className="group-title">{group.name}</div>
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
                    {ep.requiresAuth && <span title="Requires sign-in" style={{ color: 'var(--warning)', display: 'inline-flex' }}><Icon name="lock" size={14} /></span>}
                  </div>
                  <div className="meta">
                    {ep.name && <span className="small strong muted">{splitWords(ep.name)}</span>}
                    {ep.summary && <span className="small subtle truncate" style={{ maxWidth: 420 }}>— {ep.summary}</span>}
                  </div>
                </div>
                <div className="actions">
                  <Button size="sm" variant="ghost" iconOnly icon="send" title="Try it" onClick={e => { e.stopPropagation(); setSelected(ep.id); setDrawerTab('try') }} />
                  <Button size="sm" variant="ghost" iconOnly icon="edit" title="Edit" onClick={e => { e.stopPropagation(); setEditor({ mode: 'edit', endpoint: ep }) }} />
                  <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete" onClick={e => { e.stopPropagation(); remove(ep) }} />
                </div>
              </div>
            ))}
            {!isCollapsed && endpoints.length === 0 && <div className="endpoint subtle small" style={{ cursor: 'default' }}>No endpoints in this group yet.</div>}
          </div>
        )
      })}

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
      {historyOpen && <HistoryDialog history={project.history} onClose={() => setHistoryOpen(false)} onUndo={async () => { await undo(); }} />}
    </div>
  )
}

function RunBar({ run, busy, onToggle, onExport, canRun, runNote, framework }: {
  run: RunStatus | null; busy: boolean; onToggle: () => void; onExport: () => void; canRun: boolean; runNote: string | null; framework: boolean
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
        <Button size="sm" icon="download" onClick={onExport}>Export to Postman</Button>
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
