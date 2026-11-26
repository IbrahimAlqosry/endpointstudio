import { useEffect, useState } from 'react'
import { api } from '../api'
import type { BrowseResult, ProjectFileEntry, SolutionEntry } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Modal, Spinner, errorText } from './ui'

const LAST_KEY = 'es-last-folder'

function lastFolder(): string | null {
  try { return localStorage.getItem(LAST_KEY) } catch { return null }
}

/** Lets the user browse their computer to pick a .NET project or a folder. */
export function FolderPicker({ mode, onPick, onClose, initialPath }: {
  mode: 'project' | 'folder'
  onPick: (path: string) => void
  onClose: () => void
  initialPath?: string | null
}) {
  const [data, setData] = useState<BrowseResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [scan, setScan] = useState<ProjectFileEntry[] | null>(null)
  const [scanning, setScanning] = useState(false)
  const [filter, setFilter] = useState('')
  const [openSln, setOpenSln] = useState<{ sln: SolutionEntry; projects: ProjectFileEntry[] | null } | null>(null)

  const toggleSolution = async (sln: SolutionEntry) => {
    if (openSln?.sln.path === sln.path) { setOpenSln(null); return }
    setOpenSln({ sln, projects: null })
    try { setOpenSln({ sln, projects: await api.solutionProjects(sln.path) }) }
    catch (e) { setError(errorText(e)); setOpenSln(null) }
  }

  const load = async (path: string | null) => {
    setLoading(true); setError(null); setScan(null); setFilter(''); setOpenSln(null)
    try {
      const r = await api.browse(path)
      setData(r)
      if (r.path) try { localStorage.setItem(LAST_KEY, r.path) } catch { /* ignore */ }
    } catch (e) {
      setError(errorText(e))
      if (path) { try { setData(await api.browse(null)) } catch { /* ignore */ } }
    } finally { setLoading(false) }
  }

  useEffect(() => { load(initialPath ?? lastFolder()) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const runScan = async () => {
    if (!data?.path) return
    setScanning(true); setError(null)
    try { setScan(await api.findProjects(data.path)) } catch (e) { setError(errorText(e)) } finally { setScanning(false) }
  }

  const crumbs = data?.path ? buildCrumbs(data.path) : []
  const folders = (data?.folders ?? []).filter(f => f.name.toLowerCase().includes(filter.toLowerCase()))

  return (
    <Modal
      title={mode === 'project' ? 'Open a .NET project' : 'Choose a folder'}
      subtitle={mode === 'project' ? 'Browse to the folder of your API project. Projects are highlighted.' : 'Pick where the new project will be created.'}
      icon={mode === 'project' ? 'folderOpen' : 'folder'}
      onClose={onClose}
      size="wide"
      footer={<>
        {mode === 'project' && data?.path && (
          <Button icon="search" onClick={runScan} loading={scanning} style={{ marginRight: 'auto' }}>Find projects in this folder</Button>
        )}
        <Button onClick={onClose}>Cancel</Button>
        {mode === 'folder' && <Button variant="primary" icon="check" disabled={!data?.path} onClick={() => data?.path && onPick(data.path)}>Use this folder</Button>}
      </>}
    >
      <div className="path-bar">
        <button onClick={() => load(null)} title="This computer"><Icon name="monitor" size={15} /></button>
        {crumbs.map(c => (
          <span key={c.path} className="row gap-4">
            <Icon name="chevronRight" size={13} />
            <button onClick={() => load(c.path)}>{c.name}</button>
          </span>
        ))}
        {loading && <span style={{ marginLeft: 'auto' }}><Spinner /></span>}
      </div>

      {error && <div style={{ marginBottom: 10 }}><Alert kind="error">{error}</Alert></div>}

      {scan && (
        <div style={{ marginBottom: 14 }}>
          <div className="row between" style={{ marginBottom: 8 }}>
            <span className="label">{scan.length === 0 ? 'No projects found in this folder' : `${scan.length} project${scan.length > 1 ? 's' : ''} found`}</span>
            <Button size="sm" variant="ghost" onClick={() => setScan(null)}>Hide</Button>
          </div>
          {scan.length > 0 && (
            <div className="folder-list" style={{ maxHeight: 200 }}>
              {scan.map(p => <ProjectRow key={p.path} p={p} hint={relative(p.path, data?.path)} onPick={onPick} />)}
            </div>
          )}
        </div>
      )}

      {data?.path && data.folders.length > 8 && (
        <input className="input" placeholder="Filter folders…" value={filter} onChange={e => setFilter(e.target.value)} style={{ marginBottom: 10 }} />
      )}

      <div className="folder-list">
        {data?.parent && (
          <div className="folder-item" onClick={() => load(data.parent)}>
            <Icon name="arrowLeft" size={16} /><span className="muted">Up one level</span>
          </div>
        )}
        {mode === 'project' && data?.projects.map(p => <ProjectRow key={p.path} p={p} onPick={onPick} />)}
        {mode === 'project' && data?.solutions.map(sln => (
          <div key={sln.path}>
            <div className="folder-item project" onClick={() => toggleSolution(sln)}>
              <Icon name="layers" size={17} />
              <span className="strong grow truncate">{sln.name}</span>
              <span className="badge">Solution</span>
              <Icon name={openSln?.sln.path === sln.path ? 'chevronDown' : 'chevronRight'} size={15} />
            </div>
            {openSln?.sln.path === sln.path && (
              <div style={{ paddingLeft: 24, background: 'var(--bg-sunken)' }}>
                {openSln.projects === null ? <div className="folder-item" style={{ cursor: 'default' }}><Spinner /> <span className="small subtle">Reading the solution…</span></div>
                  : openSln.projects.length === 0 ? <div className="folder-item small subtle" style={{ cursor: 'default' }}>No C# projects in this solution.</div>
                    : openSln.projects.map(p => <ProjectRow key={p.path} p={p} onPick={onPick} />)}
              </div>
            )}
          </div>
        ))}
        {folders.map(f => (
          <div key={f.path} className="folder-item" onClick={() => load(f.path)}>
            <Icon name="folder" size={17} />
            <span className="grow truncate">{f.name}</span>
            <Icon name="chevronRight" size={15} />
          </div>
        ))}
        {data && !loading && folders.length === 0 && (data.projects.length + data.solutions.length === 0 || mode === 'folder') && (
          <div className="folder-item subtle" style={{ cursor: 'default' }}>This folder is empty.</div>
        )}
      </div>
    </Modal>
  )
}

function ProjectRow({ p, hint, onPick }: { p: ProjectFileEntry; hint?: string; onPick: (path: string) => void }) {
  return (
    <div className="folder-item project" onClick={() => onPick(p.path)} style={p.isWeb ? undefined : { opacity: .7 }} title={p.isWeb ? p.path : `${p.path}
(class library: usually has no endpoints)`}>
      <Icon name={p.isWeb ? 'server' : 'box'} size={17} />
      <span className="strong truncate" style={{ minWidth: 0 }}>{p.name}</span>
      <span className="tiny subtle truncate grow">{hint ?? ''}</span>
      {p.framework && <span className={`badge${p.framework.startsWith('.NET Framework') ? ' warning' : ' accent'}`}>{p.framework}</span>}
      {!p.isWeb && <span className="badge">Library</span>}
      <Icon name="arrowRight" size={15} />
    </div>
  )
}

function buildCrumbs(path: string) {
  const sep = path.includes('\\') ? '\\' : '/'
  const parts = path.split(/[\\/]/).filter(Boolean)
  const out: { name: string; path: string }[] = []
  let acc = path.startsWith('/') ? '/' : ''
  parts.forEach((p, i) => {
    acc = i === 0 ? (acc + p + (p.endsWith(':') ? sep : '')) : (acc.endsWith(sep) ? acc + p : acc + sep + p)
    out.push({ name: p, path: acc })
  })
  return out
}

function relative(path: string, root?: string | null) {
  if (!root) return path
  return path.toLowerCase().startsWith(root.toLowerCase()) ? '…' + path.slice(root.length) : path
}
