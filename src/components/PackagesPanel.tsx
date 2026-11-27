import { useEffect, useState } from 'react'
import { api } from '../api'
import type { PackageItem, PackageList, PackageSearchItem } from '../types'
import { Icon } from './Icon'
import { JobView } from './JobView'
import { Alert, Button, Empty, Modal, Spinner, Switch, errorText, useConfirm, useToast } from './ui'

/** Installed packages, updates, security problems and the package catalog. */
export function PackagesPanel({ path, projects }: { path: string; projects?: { name: string; path: string }[] }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [list, setList] = useState<PackageList | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [filter, setFilter] = useState<'all' | 'updates' | 'vulnerable'>('all')
  const [showTransitive, setShowTransitive] = useState(false)
  const [job, setJob] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [q, setQ] = useState('')

  const load = async (updates: boolean) => {
    setLoading(true); setError(null)
    try { setList(await api.packages(path, updates)) } catch (e) { setError(errorText(e)) } finally { setLoading(false) }
  }
  useEffect(() => { load(false) }, [path]) // eslint-disable-line react-hooks/exhaustive-deps

  const target = (p: PackageItem) => p.project && projects ? projects.find(x => x.name === p.project)?.path ?? path : path
  const change = async (work: () => Promise<{ jobId: string }>) => { try { setJob((await work()).jobId) } catch (e) { toast.error(errorText(e)) } }
  const update = (p: PackageItem, version: string) => change(() => api.packageAdd(target(p), p.id, version, p.dev))
  const remove = async (p: PackageItem) => {
    if (await confirm({ title: `Remove ${p.id}?`, message: 'Code that uses it will stop building until you remove those uses.', confirmText: 'Remove', danger: true }))
      change(() => api.packageRemove(target(p), p.id))
  }

  const items = (list?.packages ?? []).filter(p => (showTransitive || !p.transitive)
    && (filter === 'all' || filter === 'updates' && (p.latest || p.safeLatest) || filter === 'vulnerable' && p.vulnerabilities.length > 0)
    && (!q || p.id.toLowerCase().includes(q.toLowerCase())))
  const updates = list?.packages.filter(p => !p.transitive && (p.latest || p.safeLatest)).length ?? 0
  const vulnerable = list?.packages.filter(p => p.vulnerabilities.length > 0).length ?? 0

  return (
    <div className="col gap-12">
      <div className="row wrap gap-6">
        <div className="search" style={{ maxWidth: 280 }}><Icon name="search" size={15} /><input className="input" placeholder="Filter packages…" value={q} onChange={e => setQ(e.target.value)} /></div>
        <div className="segmented">
          {([['all', 'All'], ['updates', `Updates${list?.checkedUpdates ? ` (${updates})` : ''}`], ['vulnerable', `Security${list?.checkedUpdates ? ` (${vulnerable})` : ''}`]] as const).map(([k, label]) => (
            <button key={k} className={filter === k ? 'active' : ''} onClick={() => { setFilter(k); if (k !== 'all' && !list?.checkedUpdates) load(true) }}>{label}</button>
          ))}
        </div>
        {list?.kind === 'nuget' && <Switch checked={showTransitive} onChange={setShowTransitive} label="Include indirect" />}
        <div className="grow" />
        <Button size="sm" icon="refresh" loading={loading} onClick={() => load(true)}>Check for updates</Button>
        <Button size="sm" variant="primary" icon="plus" onClick={() => setAdding(true)}>Add package</Button>
      </div>
      {list && <div className="tiny subtle">{list.manager} · {list.packages.filter(p => !p.transitive).length} packages{list.checkedUpdates ? '' : ' · use “Check for updates” to see newer versions and security problems'}</div>}
      {list?.notes.map(n => <Alert key={n} kind="info">{n}</Alert>)}
      {error && <Alert kind="error">{error}</Alert>}
      {job && <div className="card pad"><JobView key={job} jobId={job} onDone={j => { if (j.state === 'succeeded') load(!!list?.checkedUpdates) }} /></div>}
      {!list && loading && <div className="empty"><Spinner large /><span className="muted">Reading packages…</span></div>}
      {list && items.length === 0 && <div className="card"><Empty icon="box" title={filter === 'updates' ? 'Everything is up to date' : filter === 'vulnerable' ? 'No known security problems' : 'No packages'} /></div>}
      {items.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>Package</th>{list?.packages.some(p => p.project) && <th>Project</th>}<th>Version</th><th>Safe update</th><th>Latest</th><th /></tr></thead>
            <tbody>
              {items.map(p => (
                <tr key={(p.project ?? '') + p.id}>
                  <td>
                    <div className="row gap-6">
                      <span className="strong">{p.id}</span>
                      {p.dev && <span className="badge">dev</span>}
                      {p.transitive && <span className="badge">indirect</span>}
                      {p.vulnerabilities.length > 0 && <span className="badge danger" title={p.vulnerabilities.join('\n')}><Icon name="alert" size={11} />{p.vulnerabilities[0].split(' ')[0]}</span>}
                    </div>
                  </td>
                  {list?.packages.some(x => x.project) && <td className="small subtle">{p.project}</td>}
                  <td className="mono small">{p.installed ?? p.requested ?? '—'}{p.installed && p.requested && !p.requested.includes(p.installed) && <span className="subtle"> ({p.requested})</span>}</td>
                  <td>{p.safeLatest && !p.transitive ? <button className="chip route" title="Newest version with the same major number, usually safe" onClick={() => update(p, p.safeLatest!)}>↑ {p.safeLatest}</button> : <span className="subtle">—</span>}</td>
                  <td>{p.latest && !p.transitive ? <button className="chip" title="Newest version; may include breaking changes" onClick={() => update(p, p.latest!)}>↑ {p.latest}</button> : <span className="subtle">—</span>}</td>
                  <td style={{ textAlign: 'right' }}>{!p.transitive && <Button size="sm" variant="ghost" iconOnly icon="trash" title="Remove" onClick={() => remove(p)} />}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {adding && <AddPackageDialog path={path} kind={list?.kind ?? 'nuget'} projects={projects} onClose={() => setAdding(false)} onStart={jobId => { setAdding(false); setJob(jobId) }} />}
    </div>
  )
}

function AddPackageDialog({ path, kind, projects, onClose, onStart }: { path: string; kind: 'nuget' | 'npm'; projects?: { name: string; path: string }[]; onClose: () => void; onStart: (jobId: string) => void }) {
  const toast = useToast()
  const [q, setQ] = useState('')
  const [results, setResults] = useState<PackageSearchItem[] | null>(null)
  const [pick, setPick] = useState<PackageSearchItem | null>(null)
  const [versions, setVersions] = useState<string[]>([])
  const [version, setVersion] = useState('')
  const [dev, setDev] = useState(false)
  const [project, setProject] = useState(projects?.[0]?.path ?? path)
  useEffect(() => {
    if (q.trim().length < 2) { setResults(null); return }
    const t = setTimeout(() => api.packageSearch(path, q.trim()).then(setResults).catch(e => toast.error(errorText(e))), 350)
    return () => clearTimeout(t)
  }, [q, path, toast])
  useEffect(() => { if (pick) { setVersion(pick.version); api.packageVersions(path, pick.id).then(setVersions).catch(() => setVersions([])) } }, [pick, path])
  return (
    <Modal title="Add a package" subtitle={kind === 'npm' ? 'From the npm registry.' : 'From nuget.org.'} icon="box" size="wide" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!pick} onClick={() => api.packageAdd(projects ? project : path, pick!.id, version || null, dev).then(r => onStart(r.jobId)).catch(e => toast.error(errorText(e)))}>Add {pick?.id ?? ''}</Button></>}>
      <div className="col gap-12">
        <div className="search"><Icon name="search" size={16} /><input className="input" autoFocus placeholder={kind === 'npm' ? 'e.g. axios, dayjs, zod' : 'e.g. Serilog, AutoMapper, FluentValidation'} value={q} onChange={e => { setQ(e.target.value); setPick(null) }} /></div>
        {results === null && q.length >= 2 && <div className="row small subtle"><Spinner /> Searching…</div>}
        {!pick && results && (
          <div className="card" style={{ maxHeight: 340, overflow: 'auto' }}>
            {results.length === 0 && <div className="small subtle" style={{ padding: 12 }}>Nothing found.</div>}
            {results.map(r => (
              <div key={r.id} className="git-branch" style={{ cursor: 'pointer' }} onClick={() => setPick(r)}>
                <Icon name="box" size={15} />
                <div className="grow" style={{ minWidth: 0 }}>
                  <div className="row gap-6"><span className="strong">{r.id}</span>{r.verified && <span className="badge success">verified</span>}<span className="tiny subtle">{r.version}</span></div>
                  <div className="tiny subtle truncate">{r.description}</div>
                </div>
                {r.downloads != null && <span className="tiny subtle">{r.downloads >= 1e6 ? `${(r.downloads / 1e6).toFixed(1)}M` : r.downloads >= 1e3 ? `${Math.round(r.downloads / 1e3)}k` : r.downloads} {kind === 'npm' ? 'weekly' : 'downloads'}</span>}
              </div>
            ))}
          </div>
        )}
        {pick && (
          <div className="card pad col gap-10">
            <div className="row gap-6"><span className="strong grow">{pick.id}</span><Button size="sm" variant="ghost" onClick={() => setPick(null)}>Change</Button></div>
            {pick.description && <div className="small subtle">{pick.description}</div>}
            <div className="row wrap gap-10">
              <label className="small">Version{' '}
                <select className="select" style={{ width: 'auto' }} value={version} onChange={e => setVersion(e.target.value)}>
                  {(versions.length ? versions : [pick.version]).map(v => <option key={v}>{v}</option>)}
                </select>
              </label>
              {projects && projects.length > 0 && (
                <label className="small">Project{' '}
                  <select className="select" style={{ width: 'auto' }} value={project} onChange={e => setProject(e.target.value)}>{projects.map(p => <option key={p.path} value={p.path}>{p.name}</option>)}</select>
                </label>
              )}
              {kind === 'npm' && <Switch checked={dev} onChange={setDev} label="Only for development (devDependencies)" />}
            </div>
          </div>
        )}
      </div>
    </Modal>
  )
}
