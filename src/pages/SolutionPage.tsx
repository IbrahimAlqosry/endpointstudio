import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import { GitPanel } from '../components/GitPanel'
import { FilesPanel } from '../components/FilesPanel'
import { AgentPanel } from '../components/AgentPanel'
import { BuildMenu } from '../components/BuildMenu'
import { PackagesPanel } from '../components/PackagesPanel'
import { TestsPanel } from '../components/TestsPanel'
import { TerminalPanel } from '../components/TerminalPanel'
import { Button, Empty, Spinner, errorText, useToast } from '../components/ui'
import { href, navigate } from '../router'
import type { SolutionDetails, SolutionProjectInfo } from '../types'

export const ROLE_INFO: Record<string, { icon: string; color: string; desc: string }> = {
  'API': { icon: 'server', color: 'var(--accent)', desc: 'Receives requests: holds the endpoints' },
  'Controllers': { icon: 'layers', color: 'var(--accent)', desc: 'Library with controllers used by an API' },
  'Services': { icon: 'zap', color: 'var(--post)', desc: 'Business logic called by the endpoints' },
  'Data access': { icon: 'database', color: 'var(--get)', desc: 'Talks to the database' },
  'Models': { icon: 'box', color: 'var(--patch)', desc: 'Request, response and data shapes' },
  'Library': { icon: 'file', color: 'var(--text-3)', desc: 'Shared code' },
  'Tests': { icon: 'checkCircle', color: 'var(--put)', desc: 'Automated tests' },
}

export function SolutionPage({ path, onChanged }: { path: string; onChanged: () => void }) {
  const [view, setView] = useState<'projects' | 'git' | 'files' | 'terminal' | 'packages' | 'tests' | 'agent'>('projects')
  const toast = useToast()
  const [sln, setSln] = useState<SolutionDetails | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.solution(path).then(s => { setSln(s); onChanged() }).catch(e => setError(errorText(e)))
  }, [path]) // eslint-disable-line react-hooks/exhaustive-deps

  const usedBy = useMemo(() => {
    const map: Record<string, string[]> = {}
    for (const p of sln?.projects ?? []) for (const r of p.references) (map[r] ??= []).push(p.name)
    return map
  }, [sln])

  if (error) {
    return <div className="page"><div className="card"><Empty icon="alert" title="This solution couldn't be opened" action={<a className="btn" href={href({ page: 'home' })}>Back to home</a>}>{error}</Empty></div></div>
  }
  if (!sln) return <div className="page"><div className="empty"><Spinner large /><span className="muted">Reading the solution and all of its projects…</span></div></div>

  const apis = sln.projects.filter(p => p.isWeb || p.role === 'Controllers')
  const others = sln.projects.filter(p => !(p.isWeb || p.role === 'Controllers'))
  const totalEndpoints = sln.projects.reduce((n, p) => n + (p.endpointCount ?? 0), 0)
  const versions = [...new Set(sln.projects.map(p => p.framework).filter(Boolean))]

  return (
    <div className="page">
      <div className="page-header">
        <div className="grow">
          <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>Solution</span></div>
          <div className="row wrap">
            <h1 className="page-title">{sln.name}</h1>
            {versions.map(v => <span key={v} className={`badge${v!.startsWith('.NET Framework') ? ' warning' : ''}`}>{v}</span>)}
          </div>
          <div className="row small subtle mt-8" style={{ flexWrap: 'nowrap' }}>
            <Icon name="folder" size={14} /><span className="truncate" style={{ minWidth: 0 }}>{sln.path}</span>
            <button className="btn ghost sm" style={{ flexShrink: 0 }} onClick={() => api.reveal(sln.path).catch(e => toast.error(errorText(e)))}><Icon name="folderOpen" size={14} />Show in folder</button>
            <button className="btn ghost sm" style={{ flexShrink: 0 }} onClick={() => api.openEditor(sln.path).catch(e => toast.error(errorText(e)))}><Icon name="code" size={14} />Open in VS Code</button>
          </div>
        </div>
        <div className="row gap-6">
          {apis.length > 1 && <Button icon="play" onClick={async () => { for (const p of apis) await api.run(p.path).catch(e => toast.error(errorText(e))); toast.success('Starting every API project. See them under Running.') }}>Run all APIs</Button>}
          <BuildMenu path={sln.path} label="Build solution" variant="primary" />
        </div>
      </div>


      <div className="tabs" style={{ marginTop: 0, marginBottom: 18 }}>
        <button className={`tab${view === 'projects' ? ' active' : ''}`} onClick={() => setView('projects')}><span className="row gap-6"><Icon name="layers" size={15} />Projects <span className="badge" style={{ height: 18 }}>{sln.projects.length}</span></span></button>
        <button className={`tab${view === 'git' ? ' active' : ''}`} onClick={() => setView('git')}><span className="row gap-6"><Icon name="history" size={15} />Source control</span></button>
        <button className={`tab${view === 'files' ? ' active' : ''}`} onClick={() => setView('files')}><span className="row gap-6"><Icon name="folder" size={15} />Files</span></button>
        <button className={`tab${view === 'terminal' ? ' active' : ''}`} onClick={() => setView('terminal')}><span className="row gap-6"><Icon name="monitor" size={15} />Terminal</span></button>
        <button className={`tab${view === 'packages' ? ' active' : ''}`} onClick={() => setView('packages')}><span className="row gap-6"><Icon name="box" size={15} />Packages</span></button>
        <button className={`tab${view === 'tests' ? ' active' : ''}`} onClick={() => setView('tests')}><span className="row gap-6"><Icon name="checkCircle" size={15} />Tests</span></button>
        <button className={`tab${view === 'agent' ? ' active' : ''}`} onClick={() => setView('agent')}><span className="row gap-6"><Icon name="sparkles" size={15} />AI agent</span></button>
      </div>
      {view === 'git' && <GitPanel path={sln.path} />}
      {view === 'files' && <FilesPanel root={sln.path} />}
      {view === 'terminal' && <TerminalPanel root={sln.path} kind="solution" />}
      {view === 'packages' && <PackagesPanel path={sln.path} projects={sln.projects.map(p => ({ name: p.name, path: p.path }))} />}
      {view === 'tests' && <TestsPanel path={sln.path} />}
      {view === 'agent' && <AgentPanel root={sln.path} />}
      {view === 'projects' && <>
      <div className="stats">
        <div className="card stat"><div className="stat-value">{sln.projects.length}</div><div className="stat-label">Projects</div></div>
        <div className="card stat"><div className="stat-value">{apis.length}</div><div className="stat-label">API projects</div></div>
        <div className="card stat"><div className="stat-value">{totalEndpoints}</div><div className="stat-label">Endpoints</div></div>
        <div className="card stat"><div className="stat-value">{sln.projects.reduce((n, p) => n + p.fileCount, 0)}</div><div className="stat-label">Code files</div></div>
      </div>

      <div className="section-title"><Icon name="server" size={17} /> API projects</div>
      {apis.length === 0 ? (
        <div className="card" style={{ marginBottom: 24 }}>
          <Empty icon="server" title="No API projects in this solution">This solution only contains libraries. They are usually used by API projects in other solutions.</Empty>
        </div>
      ) : (
        <div className="project-grid" style={{ marginBottom: 28 }}>
          {apis.map(p => <ProjectCard key={p.path} p={p} usedBy={usedBy[p.name] ?? []} primary />)}
        </div>
      )}

      {others.length > 0 && (
        <>
          <div className="section-title"><Icon name="layers" size={17} /> Supporting projects</div>
          <div className="project-grid">
            {others.map(p => <ProjectCard key={p.path} p={p} usedBy={usedBy[p.name] ?? []} />)}
          </div>
        </>
      )}
      </>}
    </div>
  )
}

function ProjectCard({ p, usedBy, primary }: { p: SolutionProjectInfo; usedBy: string[]; primary?: boolean }) {
  const role = ROLE_INFO[p.role] ?? ROLE_INFO.Library
  return (
    <div className="card hoverable project-card" onClick={() => navigate({ page: 'project', path: p.path })} title={p.path}>
      <div className="row">
        <div className="group-icon" style={{ color: role.color }}><Icon name={role.icon} size={17} /></div>
        <div className="grow" style={{ minWidth: 0 }}>
          <div className="strong truncate">{p.name}</div>
          <div className="tiny subtle">{p.role} · {role.desc}</div>
        </div>
      </div>
      <div className="row wrap gap-6">
        {p.framework && <span className={`badge${p.framework.startsWith('.NET Framework') ? ' warning' : ''}`}>{p.framework}</span>}
        {p.endpointCount != null && <span className="badge accent">{p.endpointCount} endpoint{p.endpointCount === 1 ? '' : 's'}</span>}
        <span className="badge">{p.fileCount} files</span>
      </div>
      {p.references.length > 0 && <div className="tiny subtle"><span className="strong">Uses:</span> {p.references.join(', ')}</div>}
      {usedBy.length > 0 && <div className="tiny subtle"><span className="strong">Used by:</span> {usedBy.join(', ')}</div>}
      <div className="row gap-6" onClick={e => e.stopPropagation()}>
        {primary && <Button size="sm" variant="primary" icon="arrowRight" onClick={() => navigate({ page: 'project', path: p.path })}>Open endpoints</Button>}
        {p.isWeb && <RunButton path={p.path} />}
        <BuildMenu path={p.path} size="sm" variant="ghost" />
      </div>
    </div>
  )
}

/** Starts or stops one project of the solution. */
function RunButton({ path }: { path: string }) {
  const toast = useToast()
  const [running, setRunning] = useState<boolean | null>(null)
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    let alive = true
    const tick = () => api.runStatus(path).then(s => { if (alive) { setRunning(s.running); setUrl(s.url) } }).catch(() => {})
    tick(); const t = setInterval(tick, 3000)
    return () => { alive = false; clearInterval(t) }
  }, [path])
  return (
    <>
      <Button size="sm" variant={running ? 'default' : 'success'} icon={running ? 'stop' : 'play'} onClick={async () => {
        try { const s = running ? await api.stop(path) : await api.run(path); setRunning(s.running); setUrl(s.url) } catch (e) { toast.error(errorText(e)) }
      }}>{running ? 'Stop' : 'Run'}</Button>
      {running && url && <a className="btn ghost sm" href={url} target="_blank" rel="noreferrer"><Icon name="external" size={13} /></a>}
    </>
  )
}
