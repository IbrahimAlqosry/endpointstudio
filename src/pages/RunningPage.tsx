import { useEffect, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import { Button, Empty, Spinner, errorText, useToast } from '../components/ui'
import { href, openPath } from '../router'
import type { RunningItem } from '../types'

/** Every API and web app started from QosriStudio, with logs and controls. */
export function RunningPage() {
  const toast = useToast()
  const [items, setItems] = useState<RunningItem[] | null>(null)
  const [logs, setLogs] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = () => api.running().then(setItems).catch(() => setItems([]))
  useEffect(() => { load(); const t = setInterval(load, 2500); return () => clearInterval(t) }, [])

  const act = async (item: RunningItem, action: 'stop' | 'restart') => {
    setBusy(item.path + action)
    try {
      if (item.kind === 'dotnet') { await api.stop(item.path); if (action === 'restart') await api.run(item.path) }
      else { await api.frontendStop(item.path); if (action === 'restart') await api.frontendRun(item.path) }
      await load()
    } catch (e) { toast.error(errorText(e)) } finally { setBusy(null) }
  }
  const stopAll = async () => {
    for (const i of items ?? []) await (i.kind === 'dotnet' ? api.stop(i.path) : api.frontendStop(i.path)).catch(() => {})
    load()
  }

  return (
    <div className="page">
      <div className="page-header">
        <div className="grow">
          <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>Running</span></div>
          <h1 className="page-title">Running apps</h1>
          <div className="muted">APIs and web apps started from QosriStudio. Start more from each project’s page or a solution.</div>
        </div>
        {items && items.some(i => i.status.running) && <Button variant="danger" icon="stop" onClick={stopAll}>Stop all</Button>}
      </div>
      {!items && <div className="empty"><Spinner large /></div>}
      {items && items.length === 0 && <div className="card"><Empty icon="play" title="Nothing is running">Open a project or app and press Run, or use “Run” next to each project on a solution page.</Empty></div>}
      <div className="col gap-10">
        {items?.map(i => (
          <div key={i.path} className="card">
            <div className="git-branch">
              <span className={`status-dot ${i.status.state}`} />
              <div className="grow" style={{ minWidth: 0 }}>
                <div className="row gap-6"><span className="strong">{i.name}</span><span className="badge">{i.kind === 'dotnet' ? '.NET API' : 'Web app'}</span><span className="badge">{i.status.state}</span></div>
                <div className="tiny subtle truncate">{i.status.url ? <a href={i.status.url} target="_blank" rel="noreferrer" className="mono">{i.status.url}</a> : i.path}</div>
              </div>
              <Button size="sm" variant="ghost" icon="fileText" onClick={() => setLogs(logs === i.path ? null : i.path)}>{logs === i.path ? 'Hide output' : 'Output'}</Button>
              <Button size="sm" variant="ghost" icon="arrowRight" onClick={() => openPath(i.path)}>Open</Button>
              {i.status.running
                ? <>
                    <Button size="sm" icon="repeat" loading={busy === i.path + 'restart'} onClick={() => act(i, 'restart')}>Restart</Button>
                    <Button size="sm" icon="stop" loading={busy === i.path + 'stop'} onClick={() => act(i, 'stop')}>Stop</Button>
                  </>
                : <Button size="sm" variant="success" icon="play" loading={busy === i.path + 'restart'} onClick={() => act(i, 'restart')}>Start again</Button>}
            </div>
            {logs === i.path && <pre className="log" style={{ margin: '0 14px 14px', maxHeight: 360, whiteSpace: 'pre-wrap' }}>{i.status.log.join('\n') || 'No output yet.'}</pre>}
          </div>
        ))}
      </div>
    </div>
  )
}
