import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api'
import type { GitBranch, GitCommit, GitCommitDetails, GitDiff, GitFile, GitStash, GitStatus } from '../types'
import { Icon } from './Icon'
import { JobView } from './JobView'
import { Alert, Button, Empty, Field, Modal, Spinner, Switch, errorText, timeAgo, useConfirm, useToast } from './ui'

type Tab = 'changes' | 'history' | 'branches' | 'stashes' | 'setup'

const KIND: Record<string, { label: string; cls: string }> = {
  added: { label: 'New', cls: 'success' },
  modified: { label: 'Changed', cls: 'accent' },
  deleted: { label: 'Deleted', cls: 'danger' },
  renamed: { label: 'Renamed', cls: 'warning' },
  copied: { label: 'Copied', cls: '' },
  conflict: { label: 'Conflict', cls: 'danger' },
}
const kindOf = (f: GitFile) => f.conflicted ? 'conflict' : f.untracked ? 'added' : f.unstaged || f.staged

/** Source control for a project, solution or app folder. */
export function GitPanel({ path }: { path: string }) {
  const toast = useToast()
  const [status, setStatus] = useState<GitStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('changes')
  const [job, setJob] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => api.gitStatus(path).then(s => { setStatus(s); setError(null) }).catch(e => setError(errorText(e))), [path])
  useEffect(() => { load() }, [load])
  useEffect(() => {
    const onFocus = () => load()
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [load])

  const act = async (work: () => Promise<unknown>, done?: string) => {
    setBusy(true)
    try { await work(); if (done) toast.success(done); await load() } catch (e) { toast.error(errorText(e)) } finally { setBusy(false) }
  }
  const startJob = (work: () => Promise<{ jobId: string }>) => work().then(r => setJob(r.jobId)).catch(e => toast.error(errorText(e)))

  if (error) return <Alert kind="error">{error}</Alert>
  if (!status) return <div className="empty"><Spinner large /></div>
  if (!status.installed) return <div className="card"><Empty icon="alert" title="Git isn’t installed" action={<a className="btn primary" href="https://git-scm.com/downloads" target="_blank" rel="noreferrer">Get Git</a>}>Install Git to keep versions of your code and share them with your team.</Empty></div>
  if (status.problem) return (
    <div className="card pad col gap-12">
      <Alert kind="warning">{status.problem}</Alert>
      <div><Button variant="primary" icon="checkCircle" loading={busy} onClick={() => act(() => api.gitTrust(path), 'This folder is now trusted.')}>Trust this folder</Button></div>
    </div>
  )
  if (!status.isRepo) return (
    <div className="card">
      <Empty icon="history" title="Versions aren’t tracked yet" action={<Button variant="primary" icon="plus" loading={busy} onClick={() => act(() => api.gitInit(path), 'Git is set up. Save your first version below.')}>Start tracking versions</Button>}>
        Git keeps every saved version of your code, lets you undo mistakes and share work with your team on GitHub, GitLab, Azure DevOps or your own server. Build output and secrets are left out automatically.
      </Empty>
    </div>
  )

  const conflicts = status.files.filter(f => f.conflicted).length
  const mainRemote = status.remotes.find(r => r.name === 'origin') ?? status.remotes[0]

  return (
    <div className="col gap-14">
      <div className="card pad">
        <div className="row wrap gap-10">
          <BranchSwitcher path={path} status={status} onChanged={load} />
          <div className="col" style={{ gap: 2 }}>
            <div className="small">
              {status.upstream ? <>Follows <span className="mono">{status.upstream}</span></> : mainRemote ? 'Not on the server yet' : 'Only on this computer'}
            </div>
            <div className="tiny subtle">
              {status.ahead > 0 && <span>{status.ahead} version{status.ahead === 1 ? '' : 's'} to send · </span>}
              {status.behind > 0 && <span>{status.behind} new on the server · </span>}
              {status.files.length} changed file{status.files.length === 1 ? '' : 's'}
            </div>
          </div>
          <div className="grow" />
          {mainRemote && <Button size="sm" variant="ghost" icon="refresh" onClick={() => startJob(() => api.gitFetch(path))}>Check server</Button>}
          {mainRemote && <Button size="sm" icon="download" disabled={!status.upstream} title={!status.upstream ? 'Send this branch first' : undefined} onClick={() => startJob(() => api.gitPull(path))}>Get latest{status.behind ? ` (${status.behind})` : ''}</Button>}
          {mainRemote && <Button size="sm" variant={status.ahead > 0 || !status.upstream ? 'primary' : 'default'} icon="send" onClick={() => startJob(() => api.gitPush(path))}>Send{status.ahead ? ` (${status.ahead})` : ''}</Button>}
          {!mainRemote && <Button size="sm" variant="primary" icon="server" onClick={() => setTab('setup')}>Connect to a server</Button>}
        </div>
        {job && <div style={{ marginTop: 12 }}><JobView key={job} jobId={job} onDone={() => load()} /></div>}
      </div>

      {status.operation && (
        <Alert kind="warning">
          <div className="row wrap gap-8">
            <span className="grow">A {status.operation} is in progress.{conflicts > 0 ? ` ${conflicts} file${conflicts === 1 ? ' has' : 's have'} conflicts: pick which version to keep for each, then save a version.` : ' All conflicts are solved: save a version to finish.'}</span>
            <Button size="sm" onClick={() => act(() => api.gitAbort(path), 'Cancelled. Everything is back to how it was before.')}>Cancel the {status.operation}</Button>
          </div>
        </Alert>
      )}
      {!status.hasIdentity && <IdentityCard path={path} onSaved={load} />}
      {!status.hasGitignore && (
        <Alert kind="info">
          <div className="row wrap gap-8"><span className="grow">There’s no ignore list, so build output, packages and secret files could be saved by mistake.</span><Button size="sm" onClick={() => act(() => api.gitIgnore(path), 'Ignore list added.')}>Add one</Button></div>
        </Alert>
      )}

      <div className="tabs" style={{ marginTop: 0 }}>
        {([['changes', 'edit', 'Changes', status.files.length], ['history', 'history', 'History', null], ['branches', 'repeat', 'Branches', null], ['stashes', 'box', 'Set aside', status.stashes || null], ['setup', 'server', 'Server & identity', null]] as const).map(([id, icon, label, n]) => (
          <button key={id} className={`tab${tab === id ? ' active' : ''}`} onClick={() => setTab(id)}><span className="row gap-6"><Icon name={icon} size={14} />{label}{n != null && <span className="badge" style={{ height: 18 }}>{n}</span>}</span></button>
        ))}
      </div>

      {tab === 'changes' && <ChangesTab path={path} status={status} onChanged={load} />}
      {tab === 'history' && <HistoryTab path={path} status={status} onChanged={load} />}
      {tab === 'branches' && <BranchesTab path={path} onChanged={load} />}
      {tab === 'stashes' && <StashesTab path={path} onChanged={load} />}
      {tab === 'setup' && <SetupTab path={path} status={status} onChanged={load} />}
    </div>
  )
}

// ---------------------------------------------------------------- Branch switcher

function BranchSwitcher({ path, status, onChanged }: { path: string; status: GitStatus; onChanged: () => void }) {
  const toast = useToast()
  const [open, setOpen] = useState(false)
  const [branches, setBranches] = useState<GitBranch[] | null>(null)
  const [newName, setNewName] = useState('')
  useEffect(() => { if (open) api.gitBranches(path).then(setBranches).catch(() => setBranches([])) }, [open, path])
  const go = async (work: () => Promise<unknown>, msg: string) => {
    try { await work(); toast.success(msg); setOpen(false); onChanged() } catch (e) { toast.error(errorText(e)) }
  }
  const local = (branches ?? []).filter(b => !b.remote)
  const remoteOnly = (branches ?? []).filter(b => b.remote && !local.some(l => l.name === b.short))
  return (
    <div style={{ position: 'relative' }}>
      <button className="branch-btn" onClick={() => setOpen(o => !o)}>
        <Icon name="repeat" size={15} />
        <span className="strong">{status.branch ?? 'old version'}</span>
        <Icon name="chevronDown" size={13} />
      </button>
      {open && (
        <>
          <div style={{ position: 'fixed', inset: 0, zIndex: 20 }} onClick={() => setOpen(false)} />
          <div className="menu" style={{ left: 0, width: 300, maxHeight: 420, overflow: 'auto' }}>
            <form className="row gap-4" style={{ padding: 6 }} onSubmit={e => { e.preventDefault(); if (newName.trim()) go(() => api.gitCreateBranch(path, newName.trim()), `Created and switched to ${newName.trim()}.`) }}>
              <input className="input" style={{ height: 30 }} placeholder="New branch, e.g. feature/orders" value={newName} onChange={e => setNewName(e.target.value.replace(/\s/g, '-'))} />
              <Button size="sm" variant="primary" type="submit" disabled={!newName.trim()}>Create</Button>
            </form>
            {!branches && <div className="row small subtle" style={{ padding: 10 }}><Spinner /> Loading…</div>}
            {local.length > 0 && <div className="menu-label">On this computer</div>}
            {local.map(b => (
              <button key={b.name} className="menu-item row gap-6" disabled={b.current} onClick={() => go(() => api.gitCheckout(path, b.name), `Switched to ${b.name}.`)}>
                <Icon name={b.current ? 'check' : 'repeat'} size={13} /><span className="grow truncate">{b.name}</span>{b.track && <span className="tiny subtle">{b.track}</span>}
              </button>
            ))}
            {remoteOnly.length > 0 && <div className="menu-label">Only on the server</div>}
            {remoteOnly.map(b => (
              <button key={b.name} className="menu-item row gap-6" onClick={() => go(() => api.gitCheckout(path, b.name), `Switched to ${b.short}.`)}>
                <Icon name="download" size={13} /><span className="grow truncate">{b.short}</span><span className="tiny subtle">{b.name.split('/')[0]}</span>
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- Changes

function ChangesTab({ path, status, onChanged }: { path: string; status: GitStatus; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const inSub = status.root != null && status.projectDir.toLowerCase() !== status.root.toLowerCase()
  const [onlyProject, setOnlyProject] = useState(false)
  const files = status.files.filter(f => !onlyProject || f.inProject)
  const [checked, setChecked] = useState<Set<string>>(() => new Set(status.files.filter(f => !f.conflicted).map(f => f.path)))
  const [active, setActive] = useState<string | null>(null)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [diff, setDiff] = useState<GitDiff | null>(null)
  const prevKeys = useRef(new Set(status.files.map(f => f.path)))

  // Newly changed files are included by default; files that are gone drop out
  const key = status.files.map(f => f.path).join('|')
  useEffect(() => {
    setChecked(prev => {
      const next = new Set<string>()
      for (const f of status.files) if (!f.conflicted && (prev.has(f.path) || !prevKeys.current.has(f.path))) next.add(f.path)
      return next
    })
    prevKeys.current = new Set(status.files.map(f => f.path))
    if (active && !status.files.some(f => f.path === active)) setActive(null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])
  useEffect(() => { if (!active) { setDiff(null); return } api.gitDiff(path, active).then(setDiff).catch(e => toast.error(errorText(e))) }, [active, path, key])

  const selected = files.filter(f => checked.has(f.path)).map(f => f.path)
  const finishing = status.operation != null && status.files.every(f => !f.conflicted)

  const commit = async () => {
    setBusy(true)
    try {
      const r = await api.gitCommit(path, message, finishing ? null : selected)
      toast.success(`Saved version ${r.hash}.`)
      setMessage(''); onChanged()
    } catch (e) { toast.error(errorText(e)) } finally { setBusy(false) }
  }
  const discard = async (list: string[]) => {
    const ok = await confirm({ title: `Throw away changes to ${list.length} file${list.length === 1 ? '' : 's'}?`, message: 'The files go back to how they were in the last saved version. New files are deleted. This can’t be undone.', confirmText: 'Throw away', danger: true })
    if (!ok) return
    try { await api.gitDiscard(path, list); toast.success('Changes thrown away.'); onChanged() } catch (e) { toast.error(errorText(e)) }
  }
  const resolve = async (file: string, keep: 'mine' | 'theirs' | 'edited') => {
    try { await api.gitResolve(path, file, keep); toast.success('Conflict solved.'); onChanged() } catch (e) { toast.error(errorText(e)) }
  }
  const stash = async () => {
    try { await api.gitStash(path, message); toast.success('Your changes were set aside. Bring them back from “Set aside”.'); onChanged() } catch (e) { toast.error(errorText(e)) }
  }

  if (status.files.length === 0) return <div className="card"><Empty icon="checkCircle" title="Everything is saved">No files have changed since the last saved version.</Empty></div>

  return (
    <div className="git-changes">
      <div className="card col" style={{ overflow: 'hidden' }}>
        <div className="row gap-6" style={{ padding: '10px 12px', borderBottom: '1px solid var(--border)' }}>
          <input type="checkbox" checked={selected.length === files.filter(f => !f.conflicted).length && selected.length > 0}
            onChange={e => setChecked(e.target.checked ? new Set(files.filter(f => !f.conflicted).map(f => f.path)) : new Set())} />
          <span className="small strong grow">{selected.length} of {files.length} selected</span>
          {inSub && <Switch checked={onlyProject} onChange={setOnlyProject} label="Only this project" />}
        </div>
        <div className="git-files">
          {files.map(f => {
            const k = KIND[kindOf(f)] ?? KIND.modified
            const name = f.path.split('/').pop()!
            const dir = f.path.slice(0, f.path.length - name.length)
            return (
              <div key={f.path} className={`git-file${active === f.path ? ' active' : ''}`} onClick={() => setActive(f.path)}>
                <input type="checkbox" disabled={f.conflicted} checked={checked.has(f.path)} onClick={e => e.stopPropagation()}
                  onChange={e => setChecked(s => { const n = new Set(s); if (e.target.checked) n.add(f.path); else n.delete(f.path); return n })} />
                <span className={`badge ${k.cls}`} style={{ minWidth: 64, justifyContent: 'center' }}>{k.label}</span>
                <span className="grow truncate" title={f.oldPath ? `${f.oldPath} → ${f.path}` : f.path}><span className="strong">{name}</span> <span className="tiny subtle">{dir}</span></span>
              </div>
            )
          })}
        </div>
        <div className="col gap-8" style={{ padding: 12, borderTop: '1px solid var(--border)' }}>
          <textarea className="textarea" rows={2} placeholder={finishing ? 'Message (optional, a default one is used)' : 'What did you change? e.g. Add the orders endpoint'} value={message} onChange={e => setMessage(e.target.value)}
            onKeyDown={e => { if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && (message.trim() || finishing)) commit() }} />
          <div className="row gap-6 wrap">
            <Button variant="primary" icon="check" loading={busy} disabled={(!message.trim() && !finishing) || (selected.length === 0 && !finishing) || !status.hasIdentity} onClick={commit}>
              {finishing ? `Finish the ${status.operation}` : `Save version${selected.length && selected.length < files.length ? ` (${selected.length})` : ''}`}
            </Button>
            <div className="grow" />
            {!status.operation && <Button size="sm" variant="ghost" icon="box" onClick={stash} title="Put all changes aside and go back to the last saved version">Set aside</Button>}
            <Button size="sm" variant="ghost" icon="undo" disabled={selected.length === 0} onClick={() => discard(selected)}>Throw away</Button>
          </div>
        </div>
      </div>

      <div className="card col" style={{ overflow: 'hidden', minHeight: 360 }}>
        {!active && <Empty icon="eye" title="Pick a file">See exactly which lines were added or removed.</Empty>}
        {active && (
          <>
            <div className="row gap-6" style={{ padding: '10px 12px', borderBottom: '1px solid var(--border)' }}>
              <span className="mono small grow truncate">{active}</span>
              {status.files.find(f => f.path === active)?.conflicted ? (
                <>
                  <Button size="sm" onClick={() => resolve(active, 'mine')}>Keep mine</Button>
                  <Button size="sm" onClick={() => resolve(active, 'theirs')}>Keep theirs</Button>
                  <Button size="sm" variant="primary" onClick={() => resolve(active, 'edited')} title="Use when you fixed the file yourself">I fixed it</Button>
                </>
              ) : (
                <Button size="sm" variant="ghost" icon="undo" onClick={() => discard([active])}>Throw away</Button>
              )}
              <Button size="sm" variant="ghost" icon="code" onClick={() => api.openEditor(`${status.root}/${active}`).catch(e => toast.error(errorText(e)))}>Open</Button>
            </div>
            <DiffView diff={diff} />
          </>
        )}
      </div>
    </div>
  )
}

export function DiffView({ diff }: { diff: GitDiff | null }) {
  const lines = useMemo(() => {
    if (!diff) return []
    const out: { kind: 'add' | 'del' | 'ctx' | 'hunk' | 'conflict'; text: string; a?: number; b?: number }[] = []
    let a = 0, b = 0, started = false
    for (const l of diff.text.split('\n')) {
      if (l.startsWith('@@')) {
        const m = /@@ -(\d+)(?:,\d+)? \+(\d+)/.exec(l)
        a = Number(m?.[1] ?? 0); b = Number(m?.[2] ?? 0); started = true
        out.push({ kind: 'hunk', text: l.replace(/^@@[^@]*@@\s?/, '') || '…' })
        continue
      }
      if (!started || l.startsWith('\\ No newline')) continue
      if (l.startsWith('+')) out.push({ kind: /^\+(<{7}|={7}|>{7})/.test(l) ? 'conflict' : 'add', text: l.slice(1), b: b++ })
      else if (l.startsWith('-')) out.push({ kind: 'del', text: l.slice(1), a: a++ })
      else if (l.startsWith(' ') || l === '') { if (l === '' && out.length && out[out.length - 1].kind === 'hunk') continue; out.push({ kind: /^ (<{7}|={7}|>{7})/.test(l) ? 'conflict' : 'ctx', text: l.slice(1), a: a++, b: b++ }) }
    }
    while (out.length && out[out.length - 1].kind === 'ctx' && out[out.length - 1].text === '') out.pop()
    return out
  }, [diff])
  if (!diff) return <div className="empty"><Spinner /></div>
  if (diff.binary) return <Empty icon="file" title="This file isn’t text">Pictures and other binary files can’t be compared line by line.</Empty>
  if (lines.length === 0) return <Empty icon="info" title="No line changes">Only the file’s name or permissions changed.</Empty>
  const added = lines.filter(l => l.kind === 'add').length, removed = lines.filter(l => l.kind === 'del').length
  return (
    <div className="col" style={{ minHeight: 0, flex: 1 }}>
      <div className="tiny subtle" style={{ padding: '6px 12px' }}><span style={{ color: 'var(--success)' }}>+{added}</span> <span style={{ color: 'var(--danger)' }}>−{removed}</span> lines{diff.tooLarge ? ' · the file is large, only the start is shown' : ''}</div>
      <div className="diff">
        {lines.map((l, i) => l.kind === 'hunk'
          ? <div key={i} className="diff-hunk">{l.text}</div>
          : <div key={i} className={`diff-line ${l.kind}`}><span className="ln">{l.kind === 'add' ? '' : l.a}</span><span className="ln">{l.kind === 'del' ? '' : l.b}</span><span className="sign">{l.kind === 'add' ? '+' : l.kind === 'del' ? '−' : ''}</span><span className="code">{l.text || ' '}</span></div>)}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- History

function HistoryTab({ path, status, onChanged }: { path: string; status: GitStatus; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [commits, setCommits] = useState<GitCommit[] | null>(null)
  const [more, setMore] = useState(true)
  const [search, setSearch] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const [tagFor, setTagFor] = useState<string | null>(null)
  const [branchFrom, setBranchFrom] = useState<string | null>(null)

  const load = useCallback(async (reset: boolean) => {
    try {
      const skip = reset ? 0 : commits?.length ?? 0
      const r = await api.gitLog(path, skip, search || undefined)
      setCommits(c => reset ? r : [...(c ?? []), ...r]); setMore(r.length === 50)
    } catch (e) { toast.error(errorText(e)) }
  }, [path, search, commits?.length, toast])
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { const t = setTimeout(() => load(true), search ? 300 : 0); return () => clearTimeout(t) }, [path, search, status.ahead, status.behind, status.branch])

  const revert = async (c: GitCommit) => {
    if (!await confirm({ title: `Undo “${c.subject}”?`, message: 'A new version is saved that reverses exactly these changes. History stays intact, so this is safe even after sharing.', confirmText: 'Undo these changes' })) return
    try { await api.gitRevert(path, c.hash); toast.success('Changes undone in a new version.'); onChanged(); load(true) } catch (e) { toast.error(errorText(e)) }
  }
  const undoLast = async () => {
    if (!await confirm({ title: 'Undo the last save?', message: 'The version is removed, but its changes stay in your files so you can fix them and save again.', confirmText: 'Undo last save' })) return
    try { await api.gitUndoCommit(path); toast.success('Last save undone. Its changes are back in “Changes”.'); onChanged(); load(true) } catch (e) { toast.error(errorText(e)) }
  }

  return (
    <div className="col gap-10">
      <div className="row gap-6 wrap">
        <div className="search" style={{ maxWidth: 360 }}><Icon name="search" size={16} /><input className="input" placeholder="Search messages…" value={search} onChange={e => setSearch(e.target.value)} /></div>
        <div className="grow" />
        {commits?.[0] && !commits[0].pushed && !search && <Button size="sm" variant="ghost" icon="undo" onClick={undoLast}>Undo last save</Button>}
      </div>
      {!commits && <div className="empty"><Spinner large /></div>}
      {commits?.length === 0 && <div className="card"><Empty icon="history" title={search ? 'No versions match' : 'No saved versions yet'}>{search ? 'Try other words.' : 'Save your first version from “Changes”.'}</Empty></div>}
      {commits && commits.length > 0 && (
        <div className="card git-log">
          {commits.map(c => (
            <div key={c.hash}>
              <div className={`git-commit${open === c.hash ? ' open' : ''}`} onClick={() => setOpen(open === c.hash ? null : c.hash)}>
                <span className={`git-dot${c.pushed ? '' : ' local'}`} title={c.pushed ? 'On the server' : 'Only on this computer'} />
                <div className="grow" style={{ minWidth: 0 }}>
                  <div className="row gap-6" style={{ flexWrap: 'nowrap' }}>
                    <span className="strong truncate">{c.subject}</span>
                    {c.refs.map(r => <span key={r} className={`badge ${r.startsWith('tag: ') ? 'warning' : r.includes('/') ? '' : 'accent'}`}>{r.replace('tag: ', '🏷 ')}</span>)}
                  </div>
                  <div className="tiny subtle">{c.author} · {timeAgo(c.date)} · <span className="mono">{c.short}</span>{!c.pushed && ' · not sent yet'}</div>
                </div>
                <Icon name={open === c.hash ? 'chevronDown' : 'chevronRight'} size={14} />
              </div>
              {open === c.hash && <CommitDetails path={path} hash={c.hash} onRevert={() => revert(c)} onTag={() => setTagFor(c.hash)} onBranch={() => setBranchFrom(c.hash)} />}
            </div>
          ))}
          {more && <div style={{ padding: 10, textAlign: 'center' }}><Button size="sm" variant="ghost" onClick={() => load(false)}>Show older versions</Button></div>}
        </div>
      )}
      {tagFor && <NameModal title="Add a label" label="Label (e.g. v1.2.0)" placeholder="v1.0.0" onClose={() => setTagFor(null)} onSubmit={async name => {
        try { await api.gitTag(path, tagFor, name); toast.success(`Labelled ${name}. It’s sent with your next “Send”.`); setTagFor(null); load(true) } catch (e) { toast.error(errorText(e)) }
      }} />}
      {branchFrom && <NameModal title="Start a branch from this version" label="Branch name" placeholder="hotfix/login" onClose={() => setBranchFrom(null)} onSubmit={async name => {
        try { await api.gitCreateBranch(path, name, branchFrom); toast.success(`Switched to the new branch ${name}.`); setBranchFrom(null); onChanged() } catch (e) { toast.error(errorText(e)) }
      }} />}
    </div>
  )
}

function CommitDetails({ path, hash, onRevert, onTag, onBranch }: { path: string; hash: string; onRevert: () => void; onTag: () => void; onBranch: () => void }) {
  const [d, setD] = useState<GitCommitDetails | null>(null)
  const [file, setFile] = useState<string | null>(null)
  const [diff, setDiff] = useState<GitDiff | null>(null)
  useEffect(() => { api.gitShow(path, hash).then(setD).catch(() => {}) }, [path, hash])
  useEffect(() => { setDiff(null); if (file) api.gitDiff(path, file, hash).then(setDiff).catch(() => {}) }, [file, path, hash])
  if (!d) return <div className="row small subtle" style={{ padding: 12 }}><Spinner /> Loading…</div>
  return (
    <div className="git-commit-body">
      {d.body && <div className="small muted" style={{ whiteSpace: 'pre-wrap', marginBottom: 10 }}>{d.body}</div>}
      <div className="tiny subtle" style={{ marginBottom: 8 }}>{new Date(d.commit.date).toLocaleString()} · {d.commit.email} · <span className="mono">{d.commit.hash}</span></div>
      <div className="col gap-4">
        {d.files.map(f => (
          <button key={f.path} className={`git-file${file === f.path ? ' active' : ''}`} onClick={() => setFile(file === f.path ? null : f.path)}>
            <span className={`badge ${KIND[f.status]?.cls ?? ''}`} style={{ minWidth: 64, justifyContent: 'center' }}>{KIND[f.status]?.label ?? f.status}</span>
            <span className="grow truncate mono small" style={{ textAlign: 'left' }}>{f.path}</span>
            <span className="tiny" style={{ color: 'var(--success)' }}>+{f.added}</span><span className="tiny" style={{ color: 'var(--danger)' }}>−{f.removed}</span>
          </button>
        ))}
      </div>
      {file && <div className="card" style={{ marginTop: 10, overflow: 'hidden' }}><DiffView diff={diff} /></div>}
      <div className="row gap-6 wrap" style={{ marginTop: 12 }}>
        <Button size="sm" icon="undo" onClick={onRevert}>Undo these changes</Button>
        <Button size="sm" variant="ghost" icon="repeat" onClick={onBranch}>Start a branch here</Button>
        <Button size="sm" variant="ghost" icon="key" onClick={onTag}>Add a label</Button>
      </div>
    </div>
  )
}

function NameModal({ title, label, placeholder, initial, onClose, onSubmit }: { title: string; label: string; placeholder?: string; initial?: string; onClose: () => void; onSubmit: (v: string) => void }) {
  const [v, setV] = useState(initial ?? '')
  return (
    <Modal title={title} icon="edit" size="narrow" onClose={onClose} footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!v.trim() || v.trim() === initial} onClick={() => onSubmit(v.trim())}>OK</Button></>}>
      <Field label={label}><input className="input" autoFocus value={v} placeholder={placeholder} onChange={e => setV(e.target.value.replace(/\s/g, '-'))} onKeyDown={e => { if (e.key === 'Enter' && v.trim()) onSubmit(v.trim()) }} /></Field>
    </Modal>
  )
}

// ---------------------------------------------------------------- Branches

function BranchesTab({ path, onChanged }: { path: string; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [branches, setBranches] = useState<GitBranch[] | null>(null)
  const [creating, setCreating] = useState(false)
  const [renaming, setRenaming] = useState<string | null>(null)
  const load = useCallback(() => api.gitBranches(path).then(setBranches).catch(e => toast.error(errorText(e))), [path, toast])
  useEffect(() => { load() }, [load])
  const run = async (work: () => Promise<unknown>, msg: string) => { try { await work(); toast.success(msg); load(); onChanged() } catch (e) { toast.error(errorText(e)) } }
  const current = branches?.find(b => b.current)
  const merge = async (b: GitBranch) => {
    if (!await confirm({ title: `Bring ${b.short} into ${current?.name}?`, message: `All changes from ${b.name} are added to ${current?.name}. If the same lines were changed on both, you’ll choose which to keep.`, confirmText: 'Merge' })) return
    try { const r = await api.gitMerge(path, b.name); if (r.conflicts) toast.error(r.message); else toast.success(r.message); load(); onChanged() } catch (e) { toast.error(errorText(e)) }
  }
  const remove = async (b: GitBranch) => {
    if (!await confirm({ title: `Delete ${b.name}?`, message: b.remote ? 'The branch is deleted from the server for everyone.' : 'Only the branch name is removed if its work is merged elsewhere.', confirmText: 'Delete', danger: true })) return
    try { await api.gitDeleteBranch(path, b.name); toast.success('Branch deleted.'); load() }
    catch (e) {
      const msg = errorText(e)
      if (msg.includes('isn\'t merged') && await confirm({ title: 'Delete unmerged work?', message: 'This branch has saved versions that exist nowhere else. They will be lost.', confirmText: 'Delete anyway', danger: true })) run(() => api.gitDeleteBranch(path, b.name, true), 'Branch deleted.')
      else if (!msg.includes('isn\'t merged')) toast.error(msg)
    }
  }
  if (!branches) return <div className="empty"><Spinner large /></div>
  const groups: [string, GitBranch[]][] = [['On this computer', branches.filter(b => !b.remote)], ['On the server', branches.filter(b => b.remote)]]
  return (
    <div className="col gap-14">
      <div><Button variant="primary" size="sm" icon="plus" onClick={() => setCreating(true)}>New branch</Button></div>
      {groups.map(([title, list]) => list.length > 0 && (
        <div key={title} className="col gap-6">
          <div className="section-title" style={{ fontSize: 14 }}>{title}</div>
          <div className="card">
            {list.map(b => (
              <div key={b.name} className="git-branch">
                <Icon name={b.current ? 'checkCircle' : 'repeat'} size={15} />
                <div className="grow" style={{ minWidth: 0 }}>
                  <div className="row gap-6"><span className="strong">{b.remote ? b.short : b.name}</span>{b.current && <span className="badge accent">current</span>}{b.remote && <span className="badge">{b.name.split('/')[0]}</span>}{b.track && <span className="badge warning">{b.track}</span>}</div>
                  <div className="tiny subtle">{b.date ? `last change ${timeAgo(b.date)}` : ''}{b.upstream ? ` · follows ${b.upstream}` : ''}</div>
                </div>
                {!b.current && <Button size="sm" onClick={() => run(() => api.gitCheckout(path, b.name), `Switched to ${b.short}.`)}>Switch</Button>}
                {!b.current && current && <Button size="sm" variant="ghost" onClick={() => merge(b)}>Merge into {current.name}</Button>}
                {!b.remote && <Button size="sm" variant="ghost" iconOnly icon="edit" title="Rename" onClick={() => setRenaming(b.name)} />}
                {!b.current && <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete" onClick={() => remove(b)} />}
              </div>
            ))}
          </div>
        </div>
      ))}
      {creating && <NameModal title="New branch" label="Name" placeholder="feature/orders" onClose={() => setCreating(false)} onSubmit={n => { setCreating(false); run(() => api.gitCreateBranch(path, n), `Created and switched to ${n}.`) }} />}
      {renaming && <NameModal title={`Rename ${renaming}`} label="New name" initial={renaming} onClose={() => setRenaming(null)} onSubmit={n => { const from = renaming; setRenaming(null); run(() => api.gitRenameBranch(path, from, n), 'Renamed.') }} />}
    </div>
  )
}

// ---------------------------------------------------------------- Stashes

function StashesTab({ path, onChanged }: { path: string; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [list, setList] = useState<GitStash[] | null>(null)
  const load = useCallback(() => api.gitStashes(path).then(setList).catch(e => toast.error(errorText(e))), [path, toast])
  useEffect(() => { load() }, [load])
  const run = async (work: () => Promise<unknown>, msg: string) => { try { await work(); toast.success(msg); load(); onChanged() } catch (e) { toast.error(errorText(e)) } }
  if (!list) return <div className="empty"><Spinner large /></div>
  if (list.length === 0) return <div className="card"><Empty icon="box" title="Nothing set aside">Use “Set aside” in Changes to park unfinished work, switch branches, and bring it back later.</Empty></div>
  return (
    <div className="card">
      {list.map(s => (
        <div key={s.ref} className="git-branch">
          <Icon name="box" size={15} />
          <div className="grow"><div className="strong">{s.message}</div><div className="tiny subtle">{s.date ? timeAgo(s.date) : ''}</div></div>
          <Button size="sm" variant="primary" onClick={() => run(() => api.gitStashPop(path, s.ref), 'Changes brought back.')}>Bring back</Button>
          <Button size="sm" variant="ghost" onClick={() => run(() => api.gitStashApply(path, s.ref), 'A copy of the changes was brought back.')}>Bring back a copy</Button>
          <Button size="sm" variant="ghost" iconOnly icon="trash" onClick={async () => { if (await confirm({ title: 'Throw these changes away?', message: 'They can’t be brought back afterwards.', confirmText: 'Throw away', danger: true })) run(() => api.gitStashDrop(path, s.ref), 'Thrown away.') }} />
        </div>
      ))}
    </div>
  )
}

// ---------------------------------------------------------------- Server & identity

function IdentityCard({ path, onSaved, status }: { path: string; onSaved: () => void; status?: GitStatus }) {
  const toast = useToast()
  const [name, setName] = useState(status?.userName ?? '')
  const [email, setEmail] = useState(status?.userEmail ?? '')
  const [global, setGlobal] = useState(true)
  return (
    <div className="card pad col gap-12">
      <div><div className="strong">Your identity</div><div className="small subtle">Every saved version records who made it. Use the email of your GitHub / GitLab / Azure DevOps account.</div></div>
      <div className="grid-2">
        <Field label="Name"><input className="input" value={name} onChange={e => setName(e.target.value)} /></Field>
        <Field label="Email"><input className="input" type="email" value={email} onChange={e => setEmail(e.target.value)} /></Field>
      </div>
      <Switch checked={global} onChange={setGlobal} label="Use for all repositories on this computer" />
      <div><Button variant="primary" size="sm" disabled={!name.trim() || !email.includes('@')} onClick={() => api.gitIdentity(path, name, email, global).then(() => { toast.success('Saved.'); onSaved() }).catch(e => toast.error(errorText(e)))}>Save</Button></div>
    </div>
  )
}

function SetupTab({ path, status, onChanged }: { path: string; status: GitStatus; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const origin = status.remotes.find(r => r.name === 'origin')
  const [url, setUrl] = useState(origin?.url ?? '')
  const save = () => api.gitSetRemote(path, 'origin', url).then(() => { toast.success('Server address saved. Use “Send” to upload.'); onChanged() }).catch(e => toast.error(errorText(e)))
  return (
    <div className="col gap-14">
      <div className="card pad col gap-12">
        <div><div className="strong">Server</div><div className="small subtle">Create an empty repository on GitHub, GitLab, Azure DevOps or Bitbucket and paste its address here.</div></div>
        <div className="row gap-6">
          <input className="input grow" value={url} placeholder="https://github.com/your-team/your-app.git" onChange={e => setUrl(e.target.value)} />
          <Button variant="primary" disabled={!url.trim() || url.trim() === origin?.url} onClick={save}>{origin ? 'Change' : 'Connect'}</Button>
        </div>
        {status.remotes.length > 0 && (
          <div className="col gap-4">
            {status.remotes.map(r => (
              <div key={r.name} className="row gap-6 small">
                <span className="badge">{r.name}</span><span className="mono grow truncate">{r.url}</span>
                <Button size="sm" variant="ghost" iconOnly icon="trash" onClick={async () => { if (await confirm({ title: `Disconnect ${r.name}?`, message: 'Only the link is removed. Nothing on the server is deleted.', confirmText: 'Disconnect', danger: true })) api.gitRemoveRemote(path, r.name).then(onChanged).catch(e => toast.error(errorText(e))) }} />
              </div>
            ))}
          </div>
        )}
        <div className="tiny subtle">Sign-in is handled by Git Credential Manager: the first time you send, a sign-in window may open.</div>
      </div>
      <IdentityCard path={path} status={status} onSaved={onChanged} />
      <div className="card pad row gap-8">
        <Icon name="folder" size={16} />
        <div className="grow small"><div className="strong">Repository folder</div><div className="mono tiny subtle">{status.root}</div></div>
        <Button size="sm" variant="ghost" onClick={() => api.gitIgnore(path).then(() => { toast.success('Ignore list updated.'); onChanged() }).catch(e => toast.error(errorText(e)))}>{status.hasGitignore ? 'Update ignore list' : 'Add ignore list'}</Button>
        <Button size="sm" variant="ghost" icon="folderOpen" onClick={() => api.reveal(status.root!).catch(() => {})}>Show</Button>
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- Clone

export function CloneDialog({ defaultFolder, onClose, onCloned }: { defaultFolder: string; onClose: () => void; onCloned: (folder: string) => void }) {
  const toast = useToast()
  const [url, setUrl] = useState('')
  const [folder, setFolder] = useState(defaultFolder)
  const [job, setJob] = useState<string | null>(null)
  return (
    <Modal title="Copy a project from Git" subtitle="Download a repository from GitHub, GitLab, Azure DevOps or any Git server." icon="download" onClose={onClose}
      footer={<><Button onClick={onClose}>Close</Button><Button variant="primary" disabled={!url.trim() || !folder.trim() || !!job} onClick={() => api.gitClone(url, folder).then(r => setJob(r.jobId)).catch(e => toast.error(errorText(e)))}>Copy</Button></>}>
      <div className="col gap-14">
        <Field label="Repository address"><input className="input" autoFocus value={url} placeholder="https://github.com/team/app.git" onChange={e => setUrl(e.target.value)} /></Field>
        <Field label="Put it in" hint="A new folder named after the repository is created here."><input className="input" value={folder} onChange={e => setFolder(e.target.value)} /></Field>
        {job && <JobView key={job} jobId={job} onDone={j => { if (j.state === 'succeeded' && j.result) onCloned(j.result) }} />}
      </div>
    </Modal>
  )
}
