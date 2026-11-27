import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { Icon } from './Icon'
import { Button, Spinner, errorText, useToast } from './ui'

type Session = { id: string; shell: string; title: string; text: string; next: number; running: boolean; exitCode: number | null }

const SHELL_NAME: Record<string, string> = { pwsh: 'PowerShell 7', powershell: 'PowerShell', cmd: 'Command Prompt', bash: 'Bash', sh: 'sh' }

/** Shell sessions in the project folder, with quick commands for .NET, npm and Git. */
export function TerminalPanel({ root, kind, scripts, packageManager }: { root: string; kind: 'dotnet' | 'solution' | 'frontend'; scripts?: string[]; packageManager?: string }) {
  const toast = useToast()
  const [shells, setShells] = useState<string[]>([])
  const [shell, setShell] = useState<string>(() => { try { return localStorage.getItem('qs-shell') ?? '' } catch { return '' } })
  const [sessions, setSessions] = useState<Session[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [input, setInput] = useState('')
  const [history, setHistory] = useState<string[]>(() => { try { return JSON.parse(localStorage.getItem('qs-term-history') ?? '[]') } catch { return [] } })
  const [hIndex, setHIndex] = useState(-1)
  const outRef = useRef<HTMLPreElement>(null)
  const stick = useRef(true)

  useEffect(() => { api.terminalShells().then(s => { setShells(s); setShell(cur => s.includes(cur) ? cur : s[0]) }).catch(() => {}) }, [])
  useEffect(() => { try { localStorage.setItem('qs-shell', shell) } catch { /* ignore */ } }, [shell])

  const start = async (command?: string) => {
    try {
      const t = await api.terminalStart(root, shell || undefined, command)
      const s: Session = { id: t.id, shell: t.shell, title: command ? command.split(' ').slice(0, 3).join(' ') : SHELL_NAME[t.shell] ?? t.shell, text: '', next: 0, running: true, exitCode: null }
      setSessions(list => [...list, s])
      setActive(t.id)
      return t.id
    } catch (e) { toast.error(errorText(e)); return null }
  }

  // Start one session when the panel opens; close them all when it goes away
  const sessionsRef = useRef<Session[]>([])
  sessionsRef.current = sessions
  useEffect(() => {
    if (shells.length && sessions.length === 0) start()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shells.length])
  useEffect(() => () => { sessionsRef.current.forEach(s => api.terminalClose(s.id).catch(() => {})) }, [])

  // Follow the output of every open session
  useEffect(() => {
    let alive = true
    const tick = async () => {
      for (const s of sessionsRef.current) {
        if (!s.running && s.next > 0) continue
        try {
          const o = await api.terminalOutput(s.id, s.next)
          if (!alive) return
          if (o.chunks.length || o.running !== s.running)
            setSessions(list => list.map(x => x.id === s.id ? { ...x, text: (x.text + o.chunks.join('')).slice(-400_000), next: o.next, running: o.running, exitCode: o.exitCode } : x))
        } catch { /* closed */ }
      }
      if (alive) timer = window.setTimeout(tick, 400)
    }
    let timer = window.setTimeout(tick, 200)
    return () => { alive = false; clearTimeout(timer) }
  }, [])

  const current = sessions.find(s => s.id === active) ?? null
  useEffect(() => { if (stick.current && outRef.current) outRef.current.scrollTop = outRef.current.scrollHeight }, [current?.text])

  const send = async (text: string, sessionId = active) => {
    if (!sessionId) { const id = await start(text); if (!id) return; return }
    try { await api.terminalInput(sessionId, text) } catch (e) { toast.error(errorText(e)) }
    if (text.trim()) {
      const h = [text, ...history.filter(x => x !== text)].slice(0, 100)
      setHistory(h); setHIndex(-1)
      try { localStorage.setItem('qs-term-history', JSON.stringify(h)) } catch { /* ignore */ }
    }
  }

  const run = (cmd: string) => {
    stick.current = true
    // Commands that keep running get their own terminal, so stopping them doesn't end your shell
    if (/(run|watch|dev|start|serve)/.test(cmd) && !/run (build|lint|test)/.test(cmd)) start(cmd)
    else if (current?.running) send(cmd)
    else start(cmd)
  }

  const close = async (id: string) => {
    await api.terminalClose(id).catch(() => {})
    setSessions(list => list.filter(s => s.id !== id))
    if (active === id) setActive(sessions.filter(s => s.id !== id).slice(-1)[0]?.id ?? null)
  }

  const pm = packageManager ?? 'npm'
  const quick: [string, string][] = kind === 'frontend'
    ? [[`${pm} install`, 'Install packages'], ...(scripts ?? []).slice(0, 8).map(s => [`${pm} run ${s}`, s] as [string, string]), [`${pm} outdated`, 'Outdated packages'], ['git status', 'git status']]
    : [['dotnet build', 'Build'], ['dotnet test', 'Test'], ['dotnet run', 'Run'], ['dotnet restore', 'Restore'], ['dotnet list package --outdated', 'Outdated packages'], ['dotnet format', 'Format code'], ['git status', 'git status']]

  return (
    <div className="term card">
      <div className="term-tabs">
        {sessions.map(s => (
          <div key={s.id} className={`ed-tab${s.id === active ? ' active' : ''}`} onClick={() => setActive(s.id)}>
            <span className={`status-dot ${s.running ? 'running' : 'stopped'}`} style={{ width: 7, height: 7 }} />
            <span>{s.title}</span>
            <button className="ed-close" onClick={e => { e.stopPropagation(); close(s.id) }}><Icon name="x" size={12} /></button>
          </div>
        ))}
        <button className="btn ghost sm" title="New terminal" onClick={() => start()}><Icon name="plus" size={14} /></button>
        <div className="grow" />
        {shells.length > 1 && (
          <select className="select" style={{ width: 'auto', height: 28, fontSize: 12 }} value={shell} onChange={e => setShell(e.target.value)} title="Shell for new terminals">
            {shells.map(s => <option key={s} value={s}>{SHELL_NAME[s] ?? s}</option>)}
          </select>
        )}
        {current?.running && <Button size="sm" variant="ghost" icon="stop" onClick={() => api.terminalStop(current.id).catch(() => {})} title="Stop what's running (ends the shell)">Stop</Button>}
        {current && <Button size="sm" variant="ghost" onClick={() => setSessions(l => l.map(x => x.id === current.id ? { ...x, text: '' } : x))}>Clear</Button>}
      </div>
      <div className="term-quick">
        {quick.map(([cmd, label]) => <button key={cmd} className="chip" title={cmd} onClick={() => run(cmd)}>{label}</button>)}
      </div>
      <pre ref={outRef} className="term-out" onScroll={e => { const el = e.currentTarget; stick.current = el.scrollTop + el.clientHeight >= el.scrollHeight - 30 }}
        onClick={() => document.getElementById('term-input')?.focus()}>
        {!current && <span className="subtle"><Spinner /> Starting…</span>}
        {current?.text}
        {current && !current.running && <span className="subtle">{`\n[ended${current.exitCode != null ? ` with code ${current.exitCode}` : ''}]`}</span>}
      </pre>
      <div className="term-input">
        <span className="mono tiny subtle">{current?.shell === 'cmd' ? '>' : current?.shell === 'bash' ? '$' : 'PS>'}</span>
        <input id="term-input" className="mono" value={input} placeholder={current?.running ? 'Type a command and press Enter…' : 'Press Enter to start a new terminal with this command'} spellCheck={false} autoComplete="off"
          onChange={e => setInput(e.target.value)}
          onKeyDown={e => {
            if (e.key === 'Enter') { const t = input; setInput(''); stick.current = true; if (current?.running) send(t); else start(t || undefined) }
            if (e.key === 'ArrowUp') { e.preventDefault(); const i = Math.min(history.length - 1, hIndex + 1); if (history[i] !== undefined) { setHIndex(i); setInput(history[i]) } }
            if (e.key === 'ArrowDown') { e.preventDefault(); const i = hIndex - 1; setHIndex(Math.max(-1, i)); setInput(i >= 0 ? history[i] : '') }
            if (e.key === 'c' && e.ctrlKey && current?.running && !window.getSelection()?.toString()) { e.preventDefault(); api.terminalStop(current.id).catch(() => {}) }
          }} />
      </div>
    </div>
  )
}
