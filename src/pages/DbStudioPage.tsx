import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { FolderPicker } from '../components/FolderPicker'
import { Icon } from '../components/Icon'
import { DataGrid } from '../components/db/DataGrid'
import { ErDiagram, tableKey } from '../components/db/ErDiagram'
import { SqlRunner } from '../components/db/SqlRunner'
import { ChangeDialog, CreateTableDialog, StructurePanel } from '../components/db/Structure'
import { Alert, Button, Choice, Empty, Field, Modal, Spinner, Switch, errorText, timeAgo, useConfirm, useToast } from '../components/ui'
import { href, navigate } from '../router'
import { usePaletteItems, type PaletteItem } from '../palette'
import type { DbConnectionInfo, DbProvider, DbRef, DbSchema, DdlChange, SavedConnection } from '../types'

const PROVIDERS: { id: DbProvider; title: string; desc: string; port: number | null; user: string }[] = [
  { id: 'SqlServer', title: 'SQL Server', desc: 'SQL Server, LocalDB or Azure SQL', port: null, user: '' },
  { id: 'PostgreSql', title: 'PostgreSQL', desc: 'PostgreSQL, Supabase, Neon…', port: 5432, user: 'postgres' },
  { id: 'MySql', title: 'MySQL', desc: 'MySQL or MariaDB', port: 3306, user: 'root' },
  { id: 'Oracle', title: 'Oracle', desc: 'Oracle Database 12c or newer', port: 1521, user: '' },
  { id: 'Sqlite', title: 'SQLite', desc: 'A database in one file', port: null, user: '' },
]
const providerTitle = (p: DbProvider) => PROVIDERS.find(x => x.id === p)?.title ?? p

export function DbStudioPage({ id, database }: { id?: string; database?: string }) {
  if (!id) return <ConnectionsScreen />
  return <Studio key={id + '|' + (database ?? '')} id={id} database={database} />
}

// ---------------------------------------------------------------- Connections

function ConnectionsScreen() {
  const toast = useToast()
  const confirm = useConfirm()
  const [list, setList] = useState<SavedConnection[] | null>(null)
  const [editing, setEditing] = useState<SavedConnection | 'new' | null>(null)
  const load = useCallback(() => { api.dbsConnections().then(l => { setList(l); if (l.length === 0) setEditing(e => e ?? 'new') }).catch(e => toast.error(errorText(e))) }, [toast])
  useEffect(() => { load() }, [load])

  return (
    <div className="page">
      <div className="page-header">
        <div className="grow">
          <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>Databases</span></div>
          <h1 className="page-title">Database Studio</h1>
          <div className="muted">See how your tables relate, browse and edit data, and change the design of SQL Server, PostgreSQL, MySQL, Oracle and SQLite databases.</div>
        </div>
        <Button variant="primary" icon="plus" onClick={() => setEditing('new')}>New connection</Button>
      </div>
      {!list && <div className="empty"><Spinner large /></div>}
      {list && list.length === 0 && !editing && <Empty icon="database" title="No saved databases yet" action={<Button variant="primary" icon="plus" onClick={() => setEditing('new')}>Connect to a database</Button>}>Passwords are stored encrypted for your Windows account only.</Empty>}
      {list && list.length > 0 && (
        <div className="project-grid">
          {list.map(c => (
            <div key={c.id} className="card hoverable pad col gap-8" onClick={() => navigate({ page: 'db', id: c.id })}>
              <div className="row gap-8" style={{ flexWrap: 'nowrap' }}>
                <div className="group-icon"><Icon name="database" size={16} /></div>
                <div className="grow" style={{ minWidth: 0 }}>
                  <div className="strong truncate">{c.name}</div>
                  <div className="tiny subtle truncate">{providerTitle(c.info.provider)} · {c.info.provider === 'Sqlite' ? c.info.server : `${c.info.server}${c.info.port ? ':' + c.info.port : ''}`}</div>
                </div>
              </div>
              <div className="row gap-6 tiny subtle" style={{ flexWrap: 'nowrap' }}>
                {c.info.integratedSecurity ? <span className="badge">Windows sign-in</span> : c.info.provider === 'Sqlite' ? <span className="badge">file</span> : <span className={`badge ${c.hasPassword ? 'success' : ''}`}><Icon name="lock" size={11} />{c.hasPassword ? 'password saved' : 'asks for password'}</span>}
                <span style={{ whiteSpace: 'nowrap' }}>{timeAgo(c.saved)}</span>
                <div className="grow" />
                <button className="btn ghost sm" onClick={e => { e.stopPropagation(); setEditing(c) }}><Icon name="edit" size={13} /></button>
                <button className="btn ghost sm" onClick={async e => {
                  e.stopPropagation()
                  if (await confirm({ title: `Forget ${c.name}?`, message: 'Only the saved connection is removed. The database isn’t touched.', confirmText: 'Forget', danger: true })) {
                    await api.dbsDeleteConnection(c.id); load()
                  }
                }}><Icon name="trash" size={13} /></button>
              </div>
            </div>
          ))}
        </div>
      )}
      {editing && <ConnectionDialog existing={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={c => { setEditing(null); navigate({ page: 'db', id: c.id }) }} />}
    </div>
  )
}

function ConnectionDialog({ existing, onClose, onSaved }: { existing: SavedConnection | null; onClose: () => void; onSaved: (c: SavedConnection) => void }) {
  const [conn, setConn] = useState<DbConnectionInfo>(existing?.info ?? { provider: 'SqlServer', server: 'localhost', port: null, integratedSecurity: true, username: '', password: '', database: null })
  const [name, setName] = useState(existing?.name ?? '')
  const [rememberPassword, setRememberPassword] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [ok, setOk] = useState<string | null>(null)
  const [filePicker, setFilePicker] = useState(false)
  const set = (p: Partial<DbConnectionInfo>) => { setConn(c => ({ ...c, ...p })); setOk(null) }
  const isOracle = conn.provider === 'Oracle', isSqlite = conn.provider === 'Sqlite'
  const pick = (id: DbProvider) => {
    if (id === conn.provider) return
    const info = PROVIDERS.find(x => x.id === id)!
    set({ provider: id, port: info.port, integratedSecurity: id === 'SqlServer', username: info.user, server: id === 'Sqlite' ? '' : conn.provider === 'Sqlite' || !conn.server ? 'localhost' : conn.server, serviceName: id === 'Oracle' ? conn.serviceName ?? '' : null, database: null })
  }
  const test = async () => {
    setBusy(true); setError(null); setOk(null)
    try {
      const ref: DbRef = existing && !conn.password ? { connectionId: existing.id } : { connection: conn }
      const r = await api.dbsConnect(ref)
      setOk(`Connected. ${r.version ? 'Server ' + r.version + '. ' : ''}${isSqlite ? '' : `${r.databases.length} ${isOracle ? 'schemas' : 'databases'} found.`}`)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }
  const save = async () => {
    setBusy(true); setError(null)
    try {
      const saved = await api.dbsSaveConnection(existing?.id ?? null, name, rememberPassword ? conn : { ...conn, password: '' })
      onSaved(saved)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }
  return (
    <Modal title={existing ? `Edit ${existing.name}` : 'Connect to a database'} icon="database" size="wide" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button icon="zap" loading={busy} onClick={test} disabled={!conn.server.trim()}>Test</Button><Button variant="primary" loading={busy} onClick={save} disabled={!conn.server.trim() || (isOracle && !conn.serviceName?.trim())}>Save and open</Button></>}>
      <div className="col gap-16">
        <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' }}>
          {PROVIDERS.map(p => <Choice key={p.id} icon="database" title={p.title} desc={p.desc} selected={conn.provider === p.id} onClick={() => pick(p.id)} />)}
        </div>
        {isSqlite ? (
          <Field label="Database file">
            <div className="row gap-6"><input className="input grow" value={conn.server} onChange={e => set({ server: e.target.value })} placeholder="C:\data\app.db" /><Button icon="folder" onClick={() => setFilePicker(true)}>Browse</Button></div>
          </Field>
        ) : (
          <>
            <div className="grid-2">
              <Field label={isOracle ? 'Host' : 'Server'}><input className="input" value={conn.server} onChange={e => set({ server: e.target.value })} /></Field>
              <Field label="Port"><input className="input" inputMode="numeric" value={conn.port ?? ''} placeholder={String(PROVIDERS.find(x => x.id === conn.provider)?.port ?? 'default')} onChange={e => set({ port: e.target.value ? Number(e.target.value.replace(/\D/g, '')) || null : null })} /></Field>
            </div>
            {isOracle && <Field label="Service name" hint="For example ORCLPDB1, XEPDB1 or FREEPDB1."><input className="input" value={conn.serviceName ?? ''} onChange={e => set({ serviceName: e.target.value })} /></Field>}
            {conn.provider === 'SqlServer' && <Switch checked={conn.integratedSecurity} onChange={v => set({ integratedSecurity: v })} label="Sign in with my Windows account" />}
            {!conn.integratedSecurity && (
              <div className="grid-2">
                <Field label="Username"><input className="input" value={conn.username ?? ''} onChange={e => set({ username: e.target.value })} autoComplete="username" /></Field>
                <Field label="Password" hint={existing?.hasPassword ? 'Leave empty to keep the saved password.' : undefined}><input className="input" type="password" value={conn.password ?? ''} onChange={e => set({ password: e.target.value })} autoComplete="current-password" /></Field>
              </div>
            )}
            {!conn.integratedSecurity && <Switch checked={rememberPassword} onChange={setRememberPassword} label="Remember the password (stored encrypted, only readable by your Windows account)" />}
            <Field label={isOracle ? 'Schema (optional)' : 'Database (optional)'} hint="Leave empty to choose each time.">
              <input className="input" value={conn.database ?? ''} onChange={e => set({ database: e.target.value || null })} />
            </Field>
          </>
        )}
        <Field label="Name (optional)" hint="How it appears in your list."><input className="input" value={name} onChange={e => setName(e.target.value)} placeholder={isSqlite ? 'app.db' : `${conn.database ?? 'database'} on ${conn.server}`} /></Field>
        {error && <Alert kind="error">{error}</Alert>}
        {ok && <Alert kind="success">{ok}</Alert>}
      </div>
      {filePicker && <FolderPicker mode="file" extensions=".db,.sqlite,.sqlite3,.db3" onPick={p => { set({ server: p }); setFilePicker(false) }} onClose={() => setFilePicker(false)} />}
    </Modal>
  )
}

// ---------------------------------------------------------------- Studio

type Selected = { kind: 'table' | 'view' | 'routine'; key: string } | null
type Mode = 'diagram' | 'object' | 'sql'

function Studio({ id, database }: { id: string; database?: string }) {
  const toast = useToast()
  const [conn, setConn] = useState<SavedConnection | null>(null)
  const [password, setPassword] = useState<string | null>(null)
  const [needPassword, setNeedPassword] = useState(false)
  const [databases, setDatabases] = useState<string[] | null>(null)
  const [schema, setSchema] = useState<DbSchema | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [mode, setMode] = useState<Mode>('diagram')
  const [sel, setSel] = useState<Selected>(null)
  const [objTab, setObjTab] = useState<'data' | 'structure' | 'script'>('data')
  const [jumpFilters, setJumpFilters] = useState<Record<string, string> | undefined>()
  const [filter, setFilter] = useState('')
  const [creating, setCreating] = useState(false)
  const [viewDialog, setViewDialog] = useState(false)
  const [change, setChange] = useState<{ change: DdlChange; title: string; after?: () => void } | null>(null)
  const [sqlSeed, setSqlSeed] = useState<string | undefined>()

  const dbRef: DbRef = useMemo(() => ({
    connectionId: id,
    database: database ?? null,
    connection: password && conn ? { ...conn.info, password } : null,
  }), [id, database, password, conn])

  useEffect(() => {
    api.dbsConnections().then(list => {
      const c = list.find(x => x.id === id)
      if (!c) { setError('That saved connection no longer exists.'); return }
      setConn(c)
      if (!c.hasPassword && !c.info.integratedSecurity && c.info.provider !== 'Sqlite') setNeedPassword(true)
    }).catch(e => setError(errorText(e)))
  }, [id])

  const loadSchema = useCallback(async () => {
    setLoading(true); setError(null)
    try { setSchema(await api.dbsSchema(dbRef)) }
    catch (e) { setError(errorText(e)) } finally { setLoading(false) }
  }, [dbRef])

  // Pick the database first when the connection doesn't name one
  useEffect(() => {
    if (!conn || needPassword) return
    const hasDb = conn.info.provider === 'Sqlite' || !!database || !!conn.info.database
    if (hasDb) { loadSchema(); return }
    api.dbsConnect(dbRef).then(r => {
      if (conn.info.provider === 'Oracle') {
        const own = (conn.info.username ?? '').toUpperCase()
        if (r.databases.length === 1 || r.databases.includes(own) && r.databases.length < 2) { navigate({ page: 'db', id, database: r.databases[0] ?? own }); return }
      }
      setDatabases(r.databases)
    }).catch(e => setError(errorText(e)))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conn, needPassword, database])

  const storageKey = `sw-db:${id}:${database ?? conn?.info.database ?? ''}`
  const tables = schema?.tables ?? []
  const selTable = sel?.kind === 'table' ? tables.find(t => tableKey(t) === sel.key) : undefined
  const selView = sel?.kind === 'view' ? schema?.views.find(v => tableKey(v) === sel.key) : undefined
  const selRoutine = sel?.kind === 'routine' ? schema?.routines.find(r => tableKey(r) === sel.key) : undefined

  const paletteItems = useMemo<PaletteItem[] | null>(() => schema && [
    { id: 'db-diagram', group: schema.database || 'Database', title: 'Diagram', icon: 'layers', keywords: 'er relations', run: () => setMode('diagram') },
    { id: 'db-sql', group: schema.database || 'Database', title: 'SQL', icon: 'code', keywords: 'query run', run: () => setMode('sql') },
    { id: 'db-newtable', group: schema.database || 'Database', title: 'New table', icon: 'plus', keywords: 'create', run: () => setCreating(true) },
    ...schema.tables.map(t => ({ id: 'tb-' + tableKey(t), group: 'Tables', title: t.name, subtitle: t.schema || undefined, icon: 'table', badge: t.rows != null ? String(t.rows) : undefined,
      keywords: t.columns.map(c => c.name).join(' '), run: () => { setSel({ kind: 'table', key: tableKey(t) }); setMode('object'); setObjTab('data'); setJumpFilters(undefined) } })),
    ...schema.views.map(v => ({ id: 'vw-' + tableKey(v), group: 'Views', title: v.name, subtitle: v.schema || undefined, icon: 'eye', run: () => { setSel({ kind: 'view', key: tableKey(v) }); setMode('object'); setObjTab('data') } })),
    ...schema.routines.map(r => ({ id: 'rt-' + tableKey(r), group: 'Procedures & functions', title: r.name, subtitle: r.kind, icon: 'zap', run: () => { setSel({ kind: 'routine', key: tableKey(r) }); setMode('object') } })),
  ], [schema])
  usePaletteItems('db', paletteItems)

  const openTable = (key: string, filters?: Record<string, string>) => {
    if (schema?.tables.some(t => tableKey(t) === key)) { setSel({ kind: 'table', key }); setMode('object'); setObjTab('data'); setJumpFilters(filters) }
    else if (schema?.views.some(v => tableKey(v) === key)) { setSel({ kind: 'view', key }); setMode('object'); setObjTab('data'); setJumpFilters(filters) }
  }

  if (error && !schema) {
    return (
      <div className="page">
        <Crumbs name={conn?.name} />
        <Alert kind="error">{error}</Alert>
        <div className="row gap-6 mt-8"><Button onClick={() => { setError(null); if (databases === null) loadSchema() }}>Try again</Button><Button variant="ghost" onClick={() => navigate({ page: 'db' })}>Back to databases</Button></div>
      </div>
    )
  }

  if (needPassword && conn) {
    return (
      <div className="page" style={{ maxWidth: 520 }}>
        <Crumbs name={conn.name} />
        <div className="card pad col gap-14">
          <div className="strong">Password for {conn.info.username} on {conn.info.server}</div>
          <PasswordForm onSubmit={p => { setPassword(p); setNeedPassword(false) }} />
          <div className="tiny subtle">It’s kept only while this page is open.</div>
        </div>
      </div>
    )
  }

  if (databases && !database && conn) {
    const isOracle = conn.info.provider === 'Oracle'
    return (
      <div className="page" style={{ maxWidth: 720 }}>
        <Crumbs name={conn.name} />
        <h2 className="section-title">Choose a {isOracle ? 'schema' : 'database'}</h2>
        <div className="choices mt-8">
          {databases.map(d => <Choice key={d} icon="database" title={d} onClick={() => navigate({ page: 'db', id, database: d })} />)}
        </div>
      </div>
    )
  }

  if (!schema) return <div className="page"><Crumbs name={conn?.name} /><div className="empty"><Spinner large /><span className="muted">Reading the database design…</span></div></div>

  const q = filter.trim().toLowerCase()
  const totalRows = tables.reduce((s, t) => s + (t.rows ?? 0), 0)
  const afterDrop = () => { setSel(null); setMode('diagram'); loadSchema() }

  return (
    <div className="page wide">
      <div className="page-header" style={{ marginBottom: 14 }}>
        <div className="grow" style={{ minWidth: 260 }}>
          <Crumbs name={conn?.name} database={schema.database} />
          <div className="row wrap">
            <h1 className="page-title">{schema.database || conn?.name}</h1>
            <span className="badge accent"><Icon name="database" size={12} />{providerTitle(schema.provider)}</span>
            <span className="badge">{schema.version}</span>
          </div>
          <div className="small subtle mt-8">{tables.length} tables · {schema.foreignKeys.length} relations · {schema.views.length} views · {schema.routines.length} procedures &amp; functions · about {totalRows.toLocaleString()} rows</div>
        </div>
        <div className="row wrap">
          {(database || databases) && <Button icon="repeat" onClick={() => navigate({ page: 'db', id })}>Switch {schema.provider === 'Oracle' ? 'schema' : 'database'}</Button>}
          <Button icon="refresh" loading={loading} onClick={loadSchema}>Refresh</Button>
          <Button icon="eye" onClick={() => setViewDialog(true)}>New view</Button>
          <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>New table</Button>
        </div>
      </div>
      {error && <div style={{ marginBottom: 12 }}><Alert kind="error">{error}</Alert></div>}

      <div className="dbs">
        <aside className="dbs-side card">
          <div className="dbs-modes">
            <button className={mode === 'diagram' ? 'active' : ''} onClick={() => setMode('diagram')}><Icon name="layers" size={15} />Diagram</button>
            <button className={mode === 'sql' ? 'active' : ''} onClick={() => setMode('sql')}><Icon name="code" size={15} />SQL</button>
          </div>
          <div className="search" style={{ minWidth: 0, margin: '8px 10px' }}><Icon name="search" size={15} /><input className="input" placeholder="Find…" value={filter} onChange={e => setFilter(e.target.value)} /></div>
          <div className="dbs-list">
            <ObjectGroup title="Tables" icon="table" items={tables.filter(t => !q || t.name.toLowerCase().includes(q)).map(t => ({ key: tableKey(t), label: t.name, sub: schema.capabilities.schemas ? t.schema : undefined, count: t.rows }))}
              selected={sel?.kind === 'table' && mode === 'object' ? sel.key : null} onPick={k => openTable(k)} />
            <ObjectGroup title="Views" icon="eye" items={schema.views.filter(v => !q || v.name.toLowerCase().includes(q)).map(v => ({ key: tableKey(v), label: v.name, sub: schema.capabilities.schemas ? v.schema : undefined }))}
              selected={sel?.kind === 'view' && mode === 'object' ? sel.key : null} onPick={k => { setSel({ kind: 'view', key: k }); setMode('object'); setObjTab('data'); setJumpFilters(undefined) }} />
            {schema.capabilities.routines && (
              <ObjectGroup title="Procedures & functions" icon="zap" items={schema.routines.filter(r => !q || r.name.toLowerCase().includes(q)).map(r => ({ key: tableKey(r), label: r.name, sub: r.kind }))}
                selected={sel?.kind === 'routine' && mode === 'object' ? sel.key : null} onPick={k => { setSel({ kind: 'routine', key: k }); setMode('object') }} />
            )}
          </div>
        </aside>

        <section className="dbs-main">
          {mode === 'diagram' && (
            tables.length === 0
              ? <Empty icon="table" title="This database has no tables yet" action={<Button variant="primary" icon="plus" onClick={() => setCreating(true)}>Create the first table</Button>} />
              : <ErDiagram schema={schema} storageKey={storageKey + ':layout'} selected={sel?.kind === 'table' ? sel.key : null}
                  onSelect={k => setSel(k ? { kind: 'table', key: k } : null)} onOpen={k => openTable(k)} />
          )}
          {mode === 'sql' && <div className="card pad"><SqlRunner dbRef={dbRef} schema={schema} storageKey={storageKey} initial={sqlSeed} onChanged={loadSchema} /></div>}
          {mode === 'object' && selTable && (
            <div className="card pad col gap-14">
              <ObjectHeader icon="table" title={selTable.name} sub={`${schema.capabilities.schemas ? selTable.schema + ' · ' : ''}${selTable.columns.length} columns${selTable.rows != null ? ` · about ${selTable.rows.toLocaleString()} rows` : ''}`}
                onSql={() => { setSqlSeed(`SELECT * FROM ${selTable.schema && schema.capabilities.schemas ? selTable.schema + '.' : ''}${selTable.name}`); setMode('sql') }} />
              <div className="tabs" style={{ marginTop: 0 }}>
                {([['data', 'list', 'Data'], ['structure', 'edit', 'Design'], ['script', 'fileText', 'Script']] as const).map(([t, icon, label]) => (
                  <button key={t} className={`tab${objTab === t ? ' active' : ''}`} onClick={() => setObjTab(t)}><span className="row gap-6"><Icon name={icon} size={14} />{label}</span></button>
                ))}
              </div>
              {objTab === 'data' && <DataGrid key={sel!.key + JSON.stringify(jumpFilters ?? {})} dbRef={dbRef} schema={schema} table={selTable} initialFilters={jumpFilters} onOpenTable={openTable} onChanged={loadSchema} />}
              {objTab === 'structure' && <StructurePanel dbRef={dbRef} schema={schema} table={selTable} onChanged={loadSchema} onOpenTable={k => { loadSchema(); setSel({ kind: 'table', key: k }) }} onDropped={afterDrop} />}
              {objTab === 'script' && <ScriptView load={() => api.dbsScript(dbRef, selTable.schema, selTable.name)} name={selTable.name} />}
            </div>
          )}
          {mode === 'object' && selView && (
            <div className="card pad col gap-14">
              <ObjectHeader icon="eye" title={selView.name} sub="View: a saved query that works like a read-only table" onDrop={() => setChange({ change: { kind: 'dropView', schema: selView.schema, table: selView.name }, title: `Delete the view ${selView.name}?`, after: afterDrop })} />
              <div className="tabs" style={{ marginTop: 0 }}>
                {([['data', 'Data'], ['script', 'Definition']] as const).map(([t, label]) => <button key={t} className={`tab${objTab === t || (t === 'data' && objTab === 'structure') ? ' active' : ''}`} onClick={() => setObjTab(t)}>{label}</button>)}
              </div>
              {objTab !== 'script' && <DataGrid key={sel!.key} dbRef={dbRef} schema={schema} readOnly initialFilters={jumpFilters} onOpenTable={openTable} onChanged={() => {}}
                table={{ schema: selView.schema, name: selView.name, rows: null, columns: [], primaryKey: [], pkName: null }} />}
              {objTab === 'script' && <pre className="log" style={{ whiteSpace: 'pre-wrap', maxHeight: 520 }}>{selView.definition ?? 'The definition isn’t available.'}</pre>}
            </div>
          )}
          {mode === 'object' && selRoutine && (
            <div className="card pad col gap-14">
              <ObjectHeader icon="zap" title={selRoutine.name} sub={selRoutine.kind} onDrop={selRoutine.kind !== 'Package' ? () => setChange({ change: { kind: 'dropRoutine', schema: selRoutine.schema, table: selRoutine.name, oldName: selRoutine.kind }, title: `Delete ${selRoutine.name}?`, after: afterDrop }) : undefined} />
              <ScriptView key={sel!.key} load={() => api.dbsRoutine(dbRef, selRoutine.schema, selRoutine.name, selRoutine.kind)} name={selRoutine.name} />
            </div>
          )}
        </section>
      </div>

      {creating && <CreateTableDialog dbRef={dbRef} schema={schema} onClose={() => setCreating(false)} onDone={async key => { setCreating(false); await loadSchema(); setSel({ kind: 'table', key }); setMode('object'); setObjTab('structure'); toast.success('Table created.') }} />}
      {viewDialog && <ViewDialog schema={schema} onClose={() => setViewDialog(false)} onSubmit={c => { setViewDialog(false); setChange({ change: c, title: `Create the view ${c.newName}`, after: () => { loadSchema().then(() => { setSel({ kind: 'view', key: tableKey({ schema: c.schema ?? '', name: c.newName! }) }); setMode('object') }) } }) }} />}
      {change && <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)} onDone={() => { const a = change.after; setChange(null); if (a) a(); else loadSchema() }} />}
    </div>
  )
}

function Crumbs({ name, database }: { name?: string; database?: string }) {
  return (
    <div className="crumbs">
      <a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} />
      <a href={href({ page: 'db' })}>Databases</a>
      {name && <><Icon name="chevronRight" size={12} /><span>{name}{database && name !== database ? ` · ${database}` : ''}</span></>}
    </div>
  )
}

function PasswordForm({ onSubmit }: { onSubmit: (p: string) => void }) {
  const [p, setP] = useState('')
  return (
    <form className="row gap-6" onSubmit={e => { e.preventDefault(); if (p) onSubmit(p) }}>
      <input className="input grow" type="password" autoFocus value={p} onChange={e => setP(e.target.value)} />
      <Button variant="primary" type="submit" disabled={!p}>Connect</Button>
    </form>
  )
}

function ObjectGroup({ title, icon, items, selected, onPick }: { title: string; icon: string; items: { key: string; label: string; sub?: string; count?: number | null }[]; selected: string | null; onPick: (k: string) => void }) {
  const [open, setOpen] = useState(true)
  return (
    <div className="dbs-group">
      <button className="dbs-group-title" onClick={() => setOpen(o => !o)}>
        <Icon name={open ? 'chevronDown' : 'chevronRight'} size={13} />{title}<span className="count">{items.length}</span>
      </button>
      {open && items.map(i => (
        <button key={i.key} className={`dbs-item${selected === i.key ? ' active' : ''}`} onClick={() => onPick(i.key)} title={i.key}>
          <Icon name={icon} size={14} />
          <span className="truncate grow">{i.label}</span>
          {i.sub && <span className="tiny subtle truncate" style={{ maxWidth: 70 }}>{i.sub}</span>}
          {i.count != null && <span className="count">{i.count >= 1000 ? Math.round(i.count / 100) / 10 + 'k' : i.count}</span>}
        </button>
      ))}
    </div>
  )
}

function ObjectHeader({ icon, title, sub, onDrop, onSql }: { icon: string; title: string; sub: string; onDrop?: () => void; onSql?: () => void }) {
  return (
    <div className="row gap-10">
      <div className="group-icon" style={{ width: 36, height: 36 }}><Icon name={icon} size={18} /></div>
      <div className="grow" style={{ minWidth: 0 }}>
        <div className="strong" style={{ fontSize: 18 }}>{title}</div>
        <div className="small subtle">{sub}</div>
      </div>
      {onSql && <Button size="sm" variant="ghost" icon="code" onClick={onSql}>Query</Button>}
      {onDrop && <Button size="sm" variant="ghost" icon="trash" onClick={onDrop}>Delete</Button>}
    </div>
  )
}

function ScriptView({ load, name }: { load: () => Promise<string>; name: string }) {
  const toast = useToast()
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { load().then(setText).catch(e => setError(errorText(e))) }, [])
  if (error) return <Alert kind="error">{error}</Alert>
  if (text === null) return <div className="row small subtle"><Spinner /> Loading…</div>
  return (
    <div className="col gap-8">
      <div className="row gap-6">
        <span className="small subtle grow">Use this to recreate {name} in another database.</span>
        <Button size="sm" icon="copy" onClick={() => { navigator.clipboard?.writeText(text); toast.info('Copied.') }}>Copy</Button>
      </div>
      <pre className="log" style={{ whiteSpace: 'pre-wrap', maxHeight: 560 }}>{text}</pre>
    </div>
  )
}

function ViewDialog({ schema, onClose, onSubmit }: { schema: DbSchema; onClose: () => void; onSubmit: (c: DdlChange) => void }) {
  const [name, setName] = useState('')
  const [sch, setSch] = useState(schema.capabilities.schemas ? (schema.schemas.includes('dbo') ? 'dbo' : schema.schemas.includes('public') ? 'public' : schema.schemas[0] ?? '') : schema.tables[0]?.schema ?? '')
  const [sql, setSql] = useState(schema.tables[0] ? `SELECT *\nFROM ${schema.tables[0].name}` : 'SELECT ')
  return (
    <Modal title="New view" subtitle="A saved query that can be browsed like a table." icon="eye" size="wide" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!name.trim() || !sql.trim()} onClick={() => onSubmit({ kind: 'createView', schema: sch, newName: name.trim(), sql })}>Continue</Button></>}>
      <div className="col gap-14">
        <div className="grid-2">
          <Field label="Name"><input className="input" autoFocus value={name} onChange={e => setName(e.target.value.replace(/\s/g, '_'))} /></Field>
          {schema.capabilities.schemas && <Field label="Schema"><select className="select" value={sch} onChange={e => setSch(e.target.value)}>{schema.schemas.map(s => <option key={s}>{s}</option>)}</select></Field>}
        </div>
        <Field label="Query"><textarea className="textarea code" rows={8} value={sql} onChange={e => setSql(e.target.value)} spellCheck={false} /></Field>
      </div>
    </Modal>
  )
}
