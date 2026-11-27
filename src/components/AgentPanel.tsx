import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import type { AgentEvent, AgentTool, GitDiff, GitStatus } from '../types'
import { DiffView } from './GitPanel'
import { Icon } from './Icon'
import { Alert, Button, Empty, Spinner, errorText, useToast } from './ui'

type Turn = { runId: string; events: AgentEvent[]; running: boolean; cost: number | null; failed: boolean }

const MODES: [string, string, string][] = [
  ['edit', 'Edit files', 'Reads and changes files. Doesn’t run commands.'],
  ['build', 'Edit, build and test', 'Also runs dotnet build/test, npm run/test and git status/diff to check its work.'],
  ['plan', 'Plan only', 'Looks at the code and explains what it would do. Changes nothing.'],
  ['auto', 'Let it decide', 'Claude Code’s auto mode: it decides which actions are safe to run.'],
]
const IDEAS = [
  'Explain how this project is structured and where the main logic lives.',
  'Add input validation to every create and update endpoint.',
  'Write unit tests for the services that have none.',
  'Find and fix bugs or risky code, then build to check.',
  'Add XML documentation comments to public classes and methods.',
]

/** Lets Claude Code (or Cursor's agent) work on the project, and shows every step and every changed file. */
export function AgentPanel({ root }: { root: string }) {
  const toast = useToast()
  const [tools, setTools] = useState<AgentTool[] | null>(null)
  const [tool, setTool] = useState('claude')
  const [mode, setMode] = useState(() => { try { return localStorage.getItem('qs-agent-mode') ?? 'edit' } catch { return 'edit' } })
  const [model, setModel] = useState('')
  const [prompt, setPrompt] = useState('')
  const [turns, setTurns] = useState<Turn[]>([])
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [changes, setChanges] = useState<GitStatus | null>(null)
  const [diff, setDiff] = useState<{ file: string; diff: GitDiff | null } | null>(null)
  const endRef = useRef<HTMLDivElement>(null)
  const busy = turns.some(t => t.running)

  useEffect(() => { api.agentTools().then(t => { setTools(t); if (!t.find(x => x.id === 'claude')?.installed && t.find(x => x.id === 'cursor')?.installed) setTool('cursor') }).catch(() => setTools([])) }, [])
  useEffect(() => { try { localStorage.setItem('qs-agent-mode', mode) } catch { /* ignore */ } }, [mode])
  useEffect(() => { endRef.current?.scrollIntoView({ block: 'end', behavior: 'smooth' }) }, [turns])

  const refreshChanges = () => api.gitStatus(root).then(s => setChanges(s.isRepo ? s : null)).catch(() => setChanges(null))
  useEffect(() => { refreshChanges() }, [root]) // eslint-disable-line react-hooks/exhaustive-deps

  const send = async () => {
    const text = prompt.trim()
    if (!text) return
    try {
      const r = await api.agentRun(root, tool, text, mode, model || null, sessionId)
      setPrompt('')
      setTurns(t => [...t, { runId: r.id, events: r.events, running: true, cost: null, failed: false }])
      follow(r.id, r.next)
    } catch (e) { toast.error(errorText(e)) }
  }

  const follow = (id: string, since: number) => {
    let next = since
    const tick = async () => {
      try {
        const s = await api.agentStatus(id, next)
        next = s.next
        if (s.sessionId) setSessionId(s.sessionId)
        setTurns(list => list.map(t => t.runId === id ? { ...t, events: [...t.events, ...s.events], running: s.running, cost: s.costUsd, failed: s.failed } : t))
        if (s.running || s.events.length > 0) setTimeout(tick, 700)
        else refreshChanges()
      } catch { /* run gone */ }
    }
    setTimeout(tick, 400)
  }

  const showDiff = async (file: string) => {
    if (diff?.file === file) { setDiff(null); return }
    setDiff({ file, diff: null })
    try { setDiff({ file, diff: await api.gitDiff(root, file) }) } catch (e) { toast.error(errorText(e)) }
  }

  const current = tools?.find(t => t.id === tool)
  const cursorEditor = tools?.find(t => t.id === 'cursor-editor')

  return (
    <div className="agent">
      <div className="agent-main card">
        <div className="agent-bar">
          <select className="select" style={{ width: 'auto' }} value={tool} onChange={e => setTool(e.target.value)}>
            {(tools ?? []).filter(t => t.id !== 'cursor-editor').map(t => <option key={t.id} value={t.id}>{t.name}{t.installed ? '' : ' (not installed)'}</option>)}
          </select>
          <select className="select" style={{ width: 'auto' }} value={mode} onChange={e => setMode(e.target.value)} title={MODES.find(m => m[0] === mode)?.[2]}>
            {MODES.filter(m => tool === 'claude' || m[0] !== 'auto').map(([k, label]) => <option key={k} value={k}>{label}</option>)}
          </select>
          <select className="select" style={{ width: 'auto' }} value={model} onChange={e => setModel(e.target.value)} title="Model">
            <option value="">Default model</option>
            {tool === 'claude' ? <><option value="opus">Opus (most capable)</option><option value="sonnet">Sonnet (balanced)</option><option value="haiku">Haiku (fastest)</option></> : <option value="auto">Auto</option>}
          </select>
          <div className="grow" />
          {sessionId && <Button size="sm" variant="ghost" icon="plus" disabled={busy} onClick={() => { setSessionId(null); setTurns([]) }}>New conversation</Button>}
          {cursorEditor?.installed && <Button size="sm" variant="ghost" icon="external" onClick={() => api.openInCursor(root).catch(e => toast.error(errorText(e)))}>Open in Cursor</Button>}
        </div>
        {current && !current.installed && <div style={{ padding: '0 14px' }}><Alert kind="warning">{current.note}</Alert></div>}

        <div className="agent-stream">
          {turns.length === 0 && (
            <Empty icon="sparkles" title={`Ask ${current?.name ?? 'the agent'} to work on this project`}>
              <div className="col gap-6" style={{ marginTop: 10, alignItems: 'stretch', textAlign: 'left' }}>
                {IDEAS.map(i => <button key={i} className="agent-idea" onClick={() => setPrompt(i)}>{i}</button>)}
              </div>
            </Empty>
          )}
          {turns.map(t => (
            <div key={t.runId} className="col gap-6">
              {t.events.map(e => <EventRow key={e.seq} e={e} />)}
              {t.running && <div className="row small subtle gap-6"><Spinner /> Working…<Button size="sm" variant="ghost" icon="stop" onClick={() => api.agentStop(t.runId)}>Stop</Button></div>}
              {!t.running && t.cost != null && <div className="tiny subtle" style={{ textAlign: 'right' }}>Cost about ${t.cost.toFixed(2)}</div>}
            </div>
          ))}
          <div ref={endRef} />
        </div>

        <div className="agent-input">
          <textarea className="textarea" rows={3} value={prompt} disabled={busy || !current?.installed}
            placeholder={sessionId ? 'Follow up: ask for changes, fixes or explanations…' : 'Describe the change, e.g. “Add a GET endpoint that returns orders by customer, with tests”'}
            onChange={e => setPrompt(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) send() }} />
          <div className="row gap-6">
            <span className="tiny subtle grow">{MODES.find(m => m[0] === mode)?.[2]} Ctrl+Enter sends.</span>
            <Button variant="primary" icon="send" loading={busy} disabled={!prompt.trim() || !current?.installed} onClick={send}>{sessionId ? 'Send' : 'Start'}</Button>
          </div>
        </div>
      </div>

      <aside className="agent-side card">
        <div className="row gap-6" style={{ padding: '10px 12px', borderBottom: '1px solid var(--border)' }}>
          <span className="strong grow">Changed files</span>
          <Button size="sm" variant="ghost" iconOnly icon="refresh" title="Refresh" onClick={refreshChanges} />
        </div>
        {!changes && <div className="small subtle" style={{ padding: 12 }}>Track this project with Git (Source control tab) to see exactly what the agent changes and undo it.</div>}
        {changes && changes.files.length === 0 && <div className="small subtle" style={{ padding: 12 }}>No changes yet.</div>}
        {changes && changes.files.map(f => (
          <div key={f.path}>
            <button className={`git-file${diff?.file === f.path ? ' active' : ''}`} onClick={() => showDiff(f.path)}>
              <span className={`badge ${f.untracked ? 'success' : f.unstaged === 'deleted' ? 'danger' : 'accent'}`}>{f.untracked ? 'new' : f.unstaged || f.staged || 'changed'}</span>
              <span className="grow truncate small" title={f.path}>{f.path}</span>
            </button>
            {diff?.file === f.path && <div style={{ maxHeight: 360, overflow: 'auto', borderBottom: '1px solid var(--border)' }}><DiffView diff={diff.diff} /></div>}
          </div>
        ))}
        {changes && changes.files.length > 0 && <div className="tiny subtle" style={{ padding: 12 }}>Review, save or throw away these changes in the Source control tab.</div>}
      </aside>
    </div>
  )
}

function EventRow({ e }: { e: AgentEvent }) {
  switch (e.kind) {
    case 'prompt': return <div className="agent-msg you"><div className="tiny subtle">You</div><div style={{ whiteSpace: 'pre-wrap' }}>{e.text}</div></div>
    case 'text': return <div className="agent-msg"><div style={{ whiteSpace: 'pre-wrap' }}>{e.text}</div></div>
    case 'tool': return <div className="agent-step"><Icon name={e.tool === 'Bash' ? 'monitor' : e.tool === 'Edit' || e.tool === 'Write' || e.tool === 'MultiEdit' ? 'edit' : 'eye'} size={13} /><span className="truncate">{e.text}</span></div>
    case 'result': return <div className="agent-step done"><Icon name="checkCircle" size={13} /><span>Finished{e.tool ? ` · ${e.tool}` : ''}</span></div>
    case 'error': return <Alert kind="error">{e.text}</Alert>
    case 'warn': return <div className="agent-step warn"><Icon name="alert" size={13} /><span className="truncate" title={e.text}>{e.text}</span></div>
    case 'info': return <div className="tiny subtle">{e.text}</div>
    case 'log': return <div className="tiny subtle mono truncate" title={e.text}>{e.text}</div>
    default: return null
  }
}
