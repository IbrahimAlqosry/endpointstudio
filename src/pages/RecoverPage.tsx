import { useEffect, useState } from 'react'
import { api } from '../api'
import { FolderPicker } from '../components/FolderPicker'
import { Icon } from '../components/Icon'
import { JobView } from '../components/JobView'
import { Alert, Button, Field, errorText, useToast } from '../components/ui'
import { href, openPath } from '../router'
import type { InspectResult, JobStatus } from '../types'

export function RecoverPage() {
  const toast = useToast()
  const [folder, setFolder] = useState('')
  const [picker, setPicker] = useState<'source' | 'output' | null>(null)
  const [result, setResult] = useState<InspectResult | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [output, setOutput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const [done, setDone] = useState<JobStatus | null>(null)
  const [defaultRoot, setDefaultRoot] = useState('')

  useEffect(() => { api.system().then(s => setDefaultRoot(s.defaultLocation)).catch(() => {}) }, [])

  const inspect = async (path: string) => {
    setFolder(path); setBusy(true); setError(null); setResult(null); setJobId(null); setDone(null)
    try {
      const r = await api.inspectPublished(path)
      setResult(r)
      setSelected(new Set(r.assemblies.filter(a => a.isOwnCode).map(a => a.name)))
      setOutput(o => o || (defaultRoot ? `${defaultRoot}\\Recovered\\${r.mainApp ?? 'App'}` : ''))
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  const recover = async () => {
    if (!result) return
    setBusy(true); setError(null); setDone(null)
    try {
      const r = await api.recover({ folder: result.folder, assemblies: [...selected], outputFolder: output, solutionName: result.mainApp })
      setJobId(r.jobId)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  const toggle = (name: string) => setSelected(s => { const n = new Set(s); if (n.has(name)) n.delete(name); else n.add(name); return n })
  const own = result?.assemblies.filter(a => a.isOwnCode) ?? []
  const others = result?.assemblies.filter(a => !a.isOwnCode) ?? []

  return (
    <div className="page" style={{ maxWidth: 980 }}>
      <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>Recover code</span></div>
      <h1 className="page-title" style={{ marginBottom: 6 }}>Recover code from a published app</h1>
      <p className="muted" style={{ marginBottom: 18 }}>Turn the .dll files of a deployed .NET app back into projects you can open, read and change here. Useful when the source code is lost, or to see what's inside a shared library.</p>
      <Alert kind="info">Only recover code you own or have permission to inspect. Recovered code works, but local variable names, comments and formatting from the original are lost.</Alert>

      <div className="card pad col gap-16 mt-16">
        <Field label="Published app folder" hint="The folder with the app's .dll files (for example the IIS site folder or a publish output). For old ASP.NET sites, pick the site folder: its bin folder is included.">
          <div className="row gap-6">
            <input className="input grow" value={folder} onChange={e => setFolder(e.target.value)} placeholder="C:\\inetpub\\wwwroot\\my-api" />
            <Button icon="folder" onClick={() => setPicker('source')}>Browse</Button>
            <Button variant="primary" icon="search" loading={busy && !result} disabled={!folder.trim()} onClick={() => inspect(folder.trim())}>Look inside</Button>
          </div>
        </Field>
        {error && <Alert kind="error">{error}</Alert>}
      </div>

      {result && (
        <div className="card pad col gap-16 mt-16">
          <div className="row between wrap">
            <div>
              <div className="strong">Found {result.assemblies.length} .NET files</div>
              <div className="small subtle">{result.mainApp ? <>The app itself is <strong>{result.mainApp}</strong>. </> : null}Your own code is selected; libraries from Microsoft and NuGet usually don't need recovering.</div>
            </div>
            <span className="badge accent">{selected.size} selected</span>
          </div>
          <AssemblyTable title="Your code" list={own} selected={selected} toggle={toggle} />
          {others.length > 0 && (
            <details className="disclose">
              <summary><Icon name="chevronRight" size={13} /> Libraries and protected files ({others.length})</summary>
              <AssemblyTable list={others} selected={selected} toggle={toggle} />
            </details>
          )}
          <Field label="Save the recovered projects in" hint="An empty or new folder. A solution file linking all projects is created there.">
            <div className="row gap-6">
              <input className="input grow" value={output} onChange={e => setOutput(e.target.value)} />
              <Button icon="folder" onClick={() => setPicker('output')}>Browse</Button>
            </div>
          </Field>
          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <Button variant="primary" size="lg" icon="wand" loading={busy && !!result} disabled={selected.size === 0 || !output.trim() || (!!jobId && !done)} onClick={recover}>Recover {selected.size} project{selected.size === 1 ? '' : 's'}</Button>
          </div>
        </div>
      )}

      {jobId && (
        <div className="card pad mt-16 col gap-16">
          <JobView key={jobId} jobId={jobId} onDone={j => { setDone(j); if (j.state === 'succeeded') toast.success('Code recovered.') }} />
          {done?.state === 'succeeded' && done.resultUrl?.startsWith('studio:') && (
            <div className="row" style={{ justifyContent: 'flex-end' }}>
              <Button variant="primary" icon="arrowRight" onClick={() => openPath(done.resultUrl!.slice(7))}>Open the recovered {done.resultUrl.endsWith('.sln') ? 'solution' : 'project'}</Button>
            </div>
          )}
        </div>
      )}

      {picker && <FolderPicker mode="folder" initialPath={(picker === 'source' ? folder : output) || null}
        onPick={p => { setPicker(null); if (picker === 'source') inspect(p); else setOutput(p) }} onClose={() => setPicker(null)} />}
    </div>
  )
}

function AssemblyTable({ title, list, selected, toggle }: { title?: string; list: InspectResult['assemblies']; selected: Set<string>; toggle: (n: string) => void }) {
  if (list.length === 0) return null
  return (
    <div>
      {title && <div className="label" style={{ marginBottom: 8 }}>{title}</div>}
      <div className="table-wrap" style={{ maxHeight: 360 }}>
        <table className="table">
          <thead><tr><th style={{ width: 36 }}></th><th>File</th><th>Version</th><th>Built for</th><th>Contents</th></tr></thead>
          <tbody>
            {list.map(a => (
              <tr key={a.path} className="clickable" onClick={() => toggle(a.name)}>
                <td><input type="checkbox" className="checkbox" checked={selected.has(a.name)} onChange={() => toggle(a.name)} onClick={e => e.stopPropagation()} /></td>
                <td>
                  <div className="strong">{a.name}</div>
                  <div className="row gap-4 wrap">
                    {a.isMainApp && <span className="badge success" style={{ height: 18 }}>The app</span>}
                    {a.controllers > 0 && <span className="badge accent" style={{ height: 18 }}>{a.controllers} controller{a.controllers === 1 ? '' : 's'}</span>}
                    {a.obfuscated && <span className="badge warning" style={{ height: 18 }} title="Names inside were scrambled on purpose; recovered code won't build">Protected (obfuscated)</span>}
                  </div>
                </td>
                <td className="small muted">{a.version}</td>
                <td className="small muted">{a.framework ?? '—'}</td>
                <td className="small muted">{a.types} types · {(a.size / 1024).toFixed(0)} KB</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
