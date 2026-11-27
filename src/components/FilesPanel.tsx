import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api'
import type { FileContent, FileSearchResult, ProjectFileItem } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Empty, Field, Modal, Spinner, errorText, useConfirm, useToast } from './ui'

const CodeEditor = lazy(() => import('./CodeEditor').then(m => ({ default: m.CodeEditor })))

type Open = { path: string; file: FileContent | null; text: string; error?: string }
const IMAGE = /\.(png|jpe?g|gif|webp|svg|ico|bmp)$/i

function fileIcon(name: string) {
  if (/\.(cs|ts|tsx|js|jsx|mjs|vue|py|java|go|rs|sql|ps1|sh)$/i.test(name)) return 'code'
  if (/\.(json|xml|csproj|props|targets|config|ya?ml|env|sln|slnx)$/i.test(name)) return 'key'
  if (IMAGE.test(name)) return 'eye'
  if (/\.(md|txt)$/i.test(name)) return 'fileText'
  return 'file'
}

function formatSize(n: number) { return n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB` }

export function FilesPanel({ root, initialFile }: { root: string; initialFile?: string }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [tree, setTree] = useState<Record<string, ProjectFileItem[]>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set(['']))
  const [hidden, setHidden] = useState(false)
  const [open, setOpen] = useState<Open[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [results, setResults] = useState<FileSearchResult | null>(null)
  const [searching, setSearching] = useState(false)
  const [dialog, setDialog] = useState<null | { kind: 'newFile' | 'newFolder'; dir: string } | { kind: 'rename'; item: ProjectFileItem }>(null)
  const [cursor, setCursor] = useState<[number, number]>([1, 1])
  const [saving, setSaving] = useState(false)
  const storeKey = 'qs-files:' + root

  const loadDir = useCallback(async (dir: string) => {
    try { const items = await api.filesList(root, dir, hidden); setTree(t => ({ ...t, [dir]: items })) }
    catch (e) { toast.error(errorText(e)) }
  }, [root, hidden, toast])

  useEffect(() => { setTree({}); [...expanded].forEach(d => loadDir(d)) }, [root, hidden]) // eslint-disable-line react-hooks/exhaustive-deps

  // Reopen the files that were open last time
  useEffect(() => {
    let paths: string[] = []
    try { paths = JSON.parse(localStorage.getItem(storeKey) ?? '[]') } catch { /* ignore */ }
    if (initialFile) paths = [...paths.filter(p => p !== initialFile), initialFile]
    paths.slice(-8).forEach(p => openFile(p, false))
    if (paths.length) setActive(initialFile ?? paths[paths.length - 1])
  }, [root]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { try { localStorage.setItem(storeKey, JSON.stringify(open.map(o => o.path))) } catch { /* ignore */ } }, [open, storeKey])

  // Warn before leaving with unsaved changes
  const dirty = open.filter(o => o.file && o.text !== o.file.text)
  useEffect(() => {
    if (dirty.length === 0) return
    const h = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', h)
    return () => window.removeEventListener('beforeunload', h)
  }, [dirty.length])

  const openFile = async (path: string, focus = true) => {
    if (focus) setActive(path)
    setOpen(list => list.some(o => o.path === path) ? list : [...list, { path, file: null, text: '' }])
    try {
      const file = await api.fileRead(root, path)
      setOpen(list => list.map(o => o.path === path ? { path, file, text: file.text } : o))
    } catch (e) {
      setOpen(list => list.map(o => o.path === path ? { ...o, error: errorText(e) } : o))
    }
  }

  const closeFile = async (path: string) => {
    const o = open.find(x => x.path === path)
    if (o?.file && o.text !== o.file.text && !await confirm({ title: `Close ${path.split('/').pop()} without saving?`, message: 'Your changes to this file will be lost.', confirmText: 'Close', danger: true })) return
    setOpen(list => list.filter(x => x.path !== path))
    if (active === path) setActive(open.filter(x => x.path !== path).slice(-1)[0]?.path ?? null)
  }

  const current = open.find(o => o.path === active) ?? null

  const save = useCallback(async () => {
    const o = open.find(x => x.path === active)
    if (!o?.file || o.text === o.file.text) return
    setSaving(true)
    try {
      const file = await api.fileWrite(root, o.path, o.text, o.file.modified)
      setOpen(list => list.map(x => x.path === o.path ? { ...x, file, text: x.text } : x))
      toast.success(`Saved ${o.path.split('/').pop()}.`, { label: 'Undo', onClick: async () => { await api.filesUndo(root); reload(o.path) } })
    } catch (e) { toast.error(errorText(e)) } finally { setSaving(false) }
  }, [open, active, root, toast]) // eslint-disable-line react-hooks/exhaustive-deps

  const reload = async (path: string) => {
    try { const file = await api.fileRead(root, path); setOpen(list => list.map(x => x.path === path ? { path, file, text: file.text } : x)) } catch (e) { toast.error(errorText(e)) }
  }

  const toggle = (dir: string) => {
    setExpanded(s => { const n = new Set(s); if (n.has(dir)) n.delete(dir); else { n.add(dir); if (!tree[dir]) loadDir(dir) } return n })
  }

  const parentOf = (p: string) => p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : ''
  const refreshParent = (p: string) => loadDir(parentOf(p))

  const remove = async (item: ProjectFileItem) => {
    if (!await confirm({ title: `Delete ${item.name}?`, message: item.isDir ? 'The folder and everything in it go to the Recycle Bin.' : 'The file goes to the Recycle Bin.', confirmText: 'Delete', danger: true })) return
    try {
      const r = await api.fileDelete(root, item.path)
      toast.success(r.message)
      setOpen(list => list.filter(o => o.path !== item.path && !o.path.startsWith(item.path + '/')))
      refreshParent(item.path)
    } catch (e) { toast.error(errorText(e)) }
  }

  // Search names and contents (debounced)
  useEffect(() => {
    if (search.trim().length < 2) { setResults(null); return }
    const t = setTimeout(async () => {
      setSearching(true)
      try { setResults(await api.filesSearch(root, search.trim())) } catch { setResults(null) } finally { setSearching(false) }
    }, 350)
    return () => clearTimeout(t)
  }, [search, root])

  const renderDir = (dir: string, depth: number): React.ReactNode => (tree[dir] ?? []).map(item => (
    <div key={item.path}>
      <div className={`ft-row${active === item.path ? ' active' : ''}`} style={{ paddingLeft: 8 + depth * 14 }}
        onClick={() => item.isDir ? toggle(item.path) : openFile(item.path)} title={item.path}>
        {item.isDir ? <Icon name={expanded.has(item.path) ? 'chevronDown' : 'chevronRight'} size={12} /> : <span style={{ width: 12 }} />}
        <Icon name={item.isDir ? (expanded.has(item.path) ? 'folderOpen' : 'folder') : fileIcon(item.name)} size={14} />
        <span className="grow truncate">{item.name}</span>
        {open.some(o => o.path === item.path && o.file && o.text !== o.file.text) && <span className="ft-dot" />}
        <span className="ft-actions" onClick={e => e.stopPropagation()}>
          {item.isDir && <button title="New file here" onClick={() => setDialog({ kind: 'newFile', dir: item.path })}><Icon name="plus" size={12} /></button>}
          <button title="Rename" onClick={() => setDialog({ kind: 'rename', item })}><Icon name="edit" size={12} /></button>
          <button title="Delete" onClick={() => remove(item)}><Icon name="trash" size={12} /></button>
        </span>
      </div>
      {item.isDir && expanded.has(item.path) && (tree[item.path] ? renderDir(item.path, depth + 1) : <div className="tiny subtle" style={{ paddingLeft: 30 + depth * 14 }}>Loading…</div>)}
    </div>
  ))

  const crumbs = useMemo(() => active?.split('/') ?? [], [active])

  return (
    <div className="files">
      <aside className="files-side card">
        <div className="row gap-4" style={{ padding: 8, borderBottom: '1px solid var(--border)' }}>
          <div className="search" style={{ minWidth: 0 }}><Icon name="search" size={14} /><input className="input" style={{ height: 32 }} placeholder="Find files or text…" value={search} onChange={e => setSearch(e.target.value)} /></div>
          <Button size="sm" variant="ghost" iconOnly icon="plus" title="New file" onClick={() => setDialog({ kind: 'newFile', dir: '' })} />
          <Button size="sm" variant="ghost" iconOnly icon="folder" title="New folder" onClick={() => setDialog({ kind: 'newFolder', dir: '' })} />
          <Button size="sm" variant="ghost" iconOnly icon="refresh" title="Refresh" onClick={() => { setTree({}); [...expanded].forEach(d => loadDir(d)) }} />
        </div>
        <div className="files-tree">
          {results ? (
            <div className="col" style={{ padding: 6 }}>
              {searching && <div className="row small subtle"><Spinner /> Searching…</div>}
              {results.files.length > 0 && <div className="menu-label">Files</div>}
              {results.files.map(f => <div key={f} className="ft-row" onClick={() => openFile(f)}><Icon name={fileIcon(f)} size={14} /><span className="truncate">{f}</span></div>)}
              {results.hits.length > 0 && <div className="menu-label">Text</div>}
              {results.hits.map((h, i) => (
                <div key={i} className="ft-row" style={{ alignItems: 'flex-start' }} onClick={() => openFile(h.path)} title={`${h.path}:${h.line}`}>
                  <span className="col" style={{ minWidth: 0 }}><span className="tiny subtle truncate">{h.path}:{h.line}</span><span className="mono tiny truncate">{h.text}</span></span>
                </div>
              ))}
              {!searching && results.files.length + results.hits.length === 0 && <div className="small subtle" style={{ padding: 8 }}>Nothing found.</div>}
            </div>
          ) : renderDir('', 0)}
        </div>
        <label className="row gap-6 tiny subtle" style={{ padding: '6px 10px', borderTop: '1px solid var(--border)' }}>
          <input type="checkbox" checked={hidden} onChange={e => setHidden(e.target.checked)} />Show bin, obj, node_modules and hidden files
        </label>
      </aside>

      <section className="files-main card">
        {open.length > 0 && (
          <div className="ed-tabs">
            {open.map(o => {
              const changed = o.file && o.text !== o.file.text
              return (
                <div key={o.path} className={`ed-tab${o.path === active ? ' active' : ''}`} onClick={() => setActive(o.path)} title={o.path} onAuxClick={e => { if (e.button === 1) closeFile(o.path) }}>
                  <Icon name={fileIcon(o.path)} size={13} />
                  <span>{o.path.split('/').pop()}</span>
                  <button className="ed-close" onClick={e => { e.stopPropagation(); closeFile(o.path) }}>{changed ? <span className="ft-dot" /> : <Icon name="x" size={12} />}</button>
                </div>
              )
            })}
          </div>
        )}
        {!current && <Empty icon="fileText" title="Open a file" >Pick a file on the left. Changes are saved with Ctrl+S and can be undone from History.</Empty>}
        {current && (
          <>
            <div className="ed-bar">
              <span className="tiny subtle truncate grow">{crumbs.join('  ›  ')}</span>
              {current.file && current.text !== current.file.text && <Button size="sm" variant="ghost" onClick={() => setOpen(l => l.map(x => x.path === current.path ? { ...x, text: x.file!.text } : x))}>Discard changes</Button>}
              <Button size="sm" variant="ghost" icon="refresh" title="Reload from disk" onClick={() => reload(current.path)} />
              <Button size="sm" variant="ghost" icon="code" title="Open in VS Code" onClick={() => api.openEditor(root.replace(/[\\/][^\\/]+\.(csproj|sln|slnx|json)$/i, '') + '/' + current.path, cursor[0]).catch(e => toast.error(errorText(e)))} />
              <Button size="sm" variant="primary" icon="check" loading={saving} disabled={!current.file || current.text === current.file.text || current.file.readOnly} onClick={save}>Save</Button>
            </div>
            <div className="ed-body">
              {current.error && <div style={{ padding: 16 }}><Alert kind="error">{current.error}</Alert></div>}
              {!current.file && !current.error && <div className="empty"><Spinner /></div>}
              {current.file && IMAGE.test(current.path) && (current.file.binary || current.path.endsWith('.svg')) && (
                <div className="ed-image"><img src={api.fileRawUrl(root, current.path)} alt={current.path} /></div>
              )}
              {current.file && current.file.binary && !IMAGE.test(current.path) && <Empty icon="file" title="This file isn’t text">{formatSize(current.file.size)}. It can’t be edited here.</Empty>}
              {current.file && current.file.tooLarge && <Empty icon="file" title="This file is too large to edit here">{formatSize(current.file.size)}. Open it in VS Code instead.</Empty>}
              {current.file && !current.file.binary && !current.file.tooLarge && !(IMAGE.test(current.path) && !current.path.endsWith('.svg')) && (
                <Suspense fallback={<div className="empty"><Spinner /></div>}>
                  <CodeEditor key={current.path} path={current.path} value={current.text} readOnly={current.file.readOnly}
                    onChange={text => setOpen(l => l.map(x => x.path === current.path ? { ...x, text } : x))}
                    onSave={save} onCursor={(l, c) => setCursor([l, c])} />
                </Suspense>
              )}
            </div>
            {current.file && !current.file.binary && (
              <div className="ed-status tiny subtle">
                <span>Line {cursor[0]}, column {cursor[1]}</span>
                <span>{current.file.encoding.toUpperCase()}</span>
                <span>{current.file.lineEnding}</span>
                <span>{formatSize(current.file.size)}</span>
                {current.file.readOnly && <span className="badge warning">read-only</span>}
                <span className="grow" />
                <span>Ctrl+S save · Ctrl+F find · Ctrl+H replace</span>
              </div>
            )}
          </>
        )}
      </section>

      {dialog && (
        <NameDialog
          title={dialog.kind === 'rename' ? `Rename ${dialog.item.name}` : dialog.kind === 'newFile' ? 'New file' : 'New folder'}
          initial={dialog.kind === 'rename' ? dialog.item.name : ''}
          hint={dialog.kind === 'rename' ? undefined : `In ${dialog.dir || 'the project folder'}. Use / to create subfolders, e.g. Services/OrderService.cs`}
          onClose={() => setDialog(null)}
          onSubmit={async name => {
            const d = dialog
            setDialog(null)
            try {
              if (d.kind === 'rename') {
                const r = await api.fileRename(root, d.item.path, name)
                setOpen(list => list.map(o => o.path === d.item.path ? { ...o, path: r.path } : o))
                if (active === d.item.path) setActive(r.path)
                refreshParent(d.item.path)
              } else {
                const path = (d.dir ? d.dir + '/' : '') + name
                await api.fileCreate(root, path, d.kind === 'newFolder', d.kind === 'newFile' ? starterFor(path) : '')
                if (d.dir) setExpanded(s => new Set(s).add(d.dir))
                await loadDir(parentOf(path))
                if (d.kind === 'newFile') openFile(path)
              }
            } catch (e) { toast.error(errorText(e)) }
          }} />
      )}
    </div>
  )
}

/** A small starting point for common new files. */
function starterFor(path: string): string {
  const name = path.split('/').pop() ?? ''
  const m = /^(.+)\.cs$/.exec(name)
  if (m) {
    const ns = path.split('/').slice(0, -1).join('.') || 'App'
    const isInterface = /^I[A-Z]/.test(m[1])
    return `namespace ${ns};\n\npublic ${isInterface ? 'interface' : 'class'} ${m[1]}\n{\n}\n`
  }
  return ''
}

function NameDialog({ title, initial, hint, onClose, onSubmit }: { title: string; initial: string; hint?: string; onClose: () => void; onSubmit: (v: string) => void }) {
  const [v, setV] = useState(initial)
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => { const dot = initial.lastIndexOf('.'); ref.current?.setSelectionRange(0, dot > 0 ? dot : initial.length) }, [initial])
  return (
    <Modal title={title} icon="edit" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!v.trim() || v.trim() === initial} onClick={() => onSubmit(v.trim())}>OK</Button></>}>
      <Field label="Name" hint={hint}><input ref={ref} className="input" autoFocus value={v} onChange={e => setV(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && v.trim()) onSubmit(v.trim()) }} /></Field>
    </Modal>
  )
}
