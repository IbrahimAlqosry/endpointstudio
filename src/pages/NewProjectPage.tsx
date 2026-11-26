import { Fragment, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { FolderPicker } from '../components/FolderPicker'
import { Icon } from '../components/Icon'
import { Alert, Button, Choice, Field, Spinner, Switch, errorText } from '../components/ui'
import { href, navigate } from '../router'
import type { DbConnectionInfo, DbTable, EndpointStyle, GenerateProjectResult } from '../types'

const OPS = [
  { id: 'list', label: 'List', needsKey: false },
  { id: 'get', label: 'Get one', needsKey: true },
  { id: 'create', label: 'Add', needsKey: false },
  { id: 'update', label: 'Update', needsKey: true },
  { id: 'delete', label: 'Delete', needsKey: true },
] as const

const CONN_KEY = 'es-last-connection'
const STEPS = ['Connect', 'Database', 'Tables', 'Settings']

type Selection = Record<string, string[]> // "schema.name" -> operations

export function NewProjectPage() {
  const [step, setStep] = useState(0)
  const [conn, setConn] = useState<DbConnectionInfo>(() => {
    try {
      const saved = localStorage.getItem(CONN_KEY)
      if (saved) return { ...JSON.parse(saved), password: '', database: null }
    } catch { /* ignore */ }
    return { provider: 'SqlServer', server: 'localhost', port: null, integratedSecurity: true, username: '', password: '', database: null }
  })
  const [databases, setDatabases] = useState<string[]>([])
  const [serverVersion, setServerVersion] = useState('')
  const [tables, setTables] = useState<DbTable[]>([])
  const [selection, setSelection] = useState<Selection>({})
  const [projectName, setProjectName] = useState('')
  const [location, setLocation] = useState('')
  const [style, setStyle] = useState<EndpointStyle>('Controller')
  const [docs, setDocs] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [picker, setPicker] = useState(false)
  const [result, setResult] = useState<GenerateProjectResult | null>(null)
  const [generating, setGenerating] = useState(false)

  useEffect(() => { api.system().then(s => setLocation(l => l || s.defaultLocation)).catch(() => {}) }, [])

  const set = (patch: Partial<DbConnectionInfo>) => setConn(c => ({ ...c, ...patch }))

  const connect = async () => {
    setBusy(true); setError(null)
    try {
      const r = await api.dbConnect(conn)
      setDatabases(r.databases)
      setServerVersion(r.version)
      try { localStorage.setItem(CONN_KEY, JSON.stringify({ ...conn, password: '', database: null })) } catch { /* ignore */ }
      setStep(1)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  const chooseDb = async (db: string) => {
    setBusy(true); setError(null)
    const next = { ...conn, database: db }
    try {
      const t = await api.dbTables(next)
      setConn(next)
      setTables(t)
      const sel: Selection = {}
      for (const tb of t) sel[key(tb)] = defaultOps(tb)
      setSelection(sel)
      setProjectName(p => p || toProjectName(db) + 'Api')
      setStep(2)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  const generate = async () => {
    setGenerating(true); setError(null)
    try {
      const r = await api.generate({
        connection: conn,
        projectName: projectName.trim(),
        location,
        style,
        includeApiDocs: docs,
        tables: Object.entries(selection).filter(([, ops]) => ops.length > 0).map(([k, operations]) => {
          const t = tables.find(x => key(x) === k)!
          return { schema: t.schema, name: t.name, operations }
        }),
      })
      setResult(r)
    } catch (e) { setError(errorText(e)) } finally { setGenerating(false) }
  }

  if (result) return <ResultView result={result} />
  if (generating) {
    return (
      <div className="page">
        <div className="card" style={{ maxWidth: 560, margin: '60px auto' }}>
          <div className="empty">
            <Spinner large />
            <h3 style={{ fontSize: 17, marginTop: 10 }}>Building your API…</h3>
            <p className="muted">Creating the project, writing endpoints for {selectedCount(selection)} table{selectedCount(selection) === 1 ? '' : 's'}, downloading packages and checking that everything compiles. The first time can take up to a minute.</p>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="page" style={{ maxWidth: 980 }}>
      <div className="crumbs"><a href={href({ page: 'home' })}>Home</a><Icon name="chevronRight" size={12} /><span>New project</span></div>
      <h1 className="page-title" style={{ marginBottom: 6 }}>Create an API from a database</h1>
      <p className="muted" style={{ marginBottom: 22 }}>Pick the tables you want and Endpoint Studio creates a complete, ready-to-run ASP.NET Core project with endpoints to read and change your data.</p>

      <div className="stepper">
        {STEPS.map((s, i) => (
          <span key={s} className="row gap-6">
            {i > 0 && <span className="step-line" />}
            <span className={`step${i === step ? ' active' : i < step ? ' done' : ''}`}>
              <span className="num">{i < step ? <Icon name="check" size={14} stroke={2.6} /> : i + 1}</span>{s}
            </span>
          </span>
        ))}
      </div>

      {error && <div style={{ marginBottom: 16 }}><Alert kind="error">{error}</Alert></div>}

      {step === 0 && (
        <div className="card pad col gap-20">
          <Field label="Database type">
            <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))' }}>
              <Choice icon="database" title="SQL Server" desc="Microsoft SQL Server, LocalDB or Azure SQL" selected={conn.provider === 'SqlServer'} onClick={() => set({ provider: 'SqlServer', port: null, integratedSecurity: true, server: conn.provider === 'SqlServer' ? conn.server : 'localhost' })} />
              <Choice icon="database" title="MySQL" desc="MySQL or MariaDB" selected={conn.provider === 'MySql'} onClick={() => set({ provider: 'MySql', integratedSecurity: false, username: conn.username || 'root', server: conn.provider === 'MySql' ? conn.server : 'localhost' })} />
            </div>
          </Field>
          <div className="grid-2">
            <Field label="Server" hint={conn.provider === 'SqlServer' ? 'e.g. localhost, .\\SQLEXPRESS or (localdb)\\MSSQLLocalDB' : 'e.g. localhost'}>
              <input className="input" value={conn.server} onChange={e => set({ server: e.target.value })} />
            </Field>
            <Field label="Port" hint={conn.provider === 'SqlServer' ? 'Leave empty for the default' : 'Default is 3306'}>
              <input className="input" inputMode="numeric" value={conn.port ?? ''} onChange={e => set({ port: e.target.value ? Number(e.target.value.replace(/\D/g, '')) || null : null })} placeholder={conn.provider === 'SqlServer' ? '1433' : '3306'} />
            </Field>
          </div>
          {conn.provider === 'SqlServer' && (
            <Switch checked={conn.integratedSecurity} onChange={v => set({ integratedSecurity: v })} label="Sign in with my Windows account" />
          )}
          {!conn.integratedSecurity && (
            <div className="grid-2">
              <Field label="Username"><input className="input" value={conn.username ?? ''} onChange={e => set({ username: e.target.value })} autoComplete="username" /></Field>
              <Field label="Password"><input className="input" type="password" value={conn.password ?? ''} onChange={e => set({ password: e.target.value })} autoComplete="current-password" /></Field>
            </div>
          )}
          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <Button variant="primary" icon="zap" loading={busy} onClick={connect} disabled={!conn.server.trim()}>Connect</Button>
          </div>
        </div>
      )}

      {step === 1 && (
        <DatabaseStep databases={databases} version={serverVersion} busy={busy} onBack={() => setStep(0)} onPick={chooseDb} />
      )}

      {step === 2 && (
        <TablesStep tables={tables} selection={selection} onChange={setSelection} database={conn.database!} onBack={() => setStep(1)} onNext={() => setStep(3)} />
      )}

      {step === 3 && (
        <div className="card pad col gap-20">
          <div className="grid-2">
            <Field label="Project name" hint="Letters and numbers, e.g. ShopApi">
              <input className="input" value={projectName} onChange={e => setProjectName(e.target.value.replace(/[^A-Za-z0-9_.]/g, ''))} />
            </Field>
            <Field label="Save in" hint={location && projectName ? `Will be created in …\\${shortPath(location)}\\${projectName}` : undefined}>
              <div className="row gap-6">
                <input className="input grow" value={location} onChange={e => setLocation(e.target.value)} />
                <Button icon="folder" onClick={() => setPicker(true)}>Browse</Button>
              </div>
            </Field>
          </div>
          <Field label="Endpoint style">
            <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' }}>
              <Choice icon="layers" title="Controllers" desc="Classic structure: one controller per table. Familiar to most .NET teams." selected={style === 'Controller'} onClick={() => setStyle('Controller')} />
              <Choice icon="zap" title="Minimal APIs" desc="Modern, lightweight style. One small file per table." selected={style === 'MinimalApi'} onClick={() => setStyle('MinimalApi')} />
            </div>
          </Field>
          <div className="card pad row" style={{ background: 'var(--bg-sunken)' }}>
            <div className="group-icon"><Icon name="fileText" size={16} /></div>
            <div className="grow">
              <div className="strong">Interactive API documentation</div>
              <div className="small subtle">Adds a docs page to your API where every endpoint can be explored and tried in the browser.</div>
            </div>
            <Switch checked={docs} onChange={setDocs} />
          </div>
          <Alert kind="info">
            {selectedCount(selection)} table{selectedCount(selection) === 1 ? '' : 's'} with {Object.values(selection).reduce((n, o) => n + o.length, 0)} endpoints will be created from <strong>{conn.database}</strong>.
            {!conn.integratedSecurity && ' The database password will be stored in the project’s settings file.'}
          </Alert>
          <div className="row between">
            <Button icon="arrowLeft" onClick={() => setStep(2)}>Back</Button>
            <Button variant="primary" size="lg" icon="rocket" onClick={generate} disabled={!projectName || !/^[A-Za-z]/.test(projectName) || !location.trim()}>Create project</Button>
          </div>
        </div>
      )}

      {picker && <FolderPicker mode="folder" initialPath={location || null} onPick={p => { setLocation(p); setPicker(false) }} onClose={() => setPicker(false)} />}
    </div>
  )
}

function DatabaseStep({ databases, version, busy, onBack, onPick }: { databases: string[]; version: string; busy: boolean; onBack: () => void; onPick: (db: string) => void }) {
  const [filter, setFilter] = useState('')
  const list = databases.filter(d => d.toLowerCase().includes(filter.toLowerCase()))
  return (
    <div className="card pad col gap-16">
      <div className="row between wrap">
        <div>
          <div className="strong">Choose a database</div>
          <div className="small subtle">Connected · server version {version}</div>
        </div>
        {busy && <Spinner />}
      </div>
      {databases.length > 6 && <input className="input" placeholder="Filter databases…" value={filter} onChange={e => setFilter(e.target.value)} autoFocus />}
      {databases.length === 0 ? (
        <Alert kind="warning">No databases were found on this server, or this login can’t see any.</Alert>
      ) : (
        <div className="choices">
          {list.map(d => <Choice key={d} icon="database" title={d} onClick={() => !busy && onPick(d)} />)}
        </div>
      )}
      <div><Button icon="arrowLeft" onClick={onBack}>Back</Button></div>
    </div>
  )
}

function TablesStep({ tables, selection, onChange, database, onBack, onNext }: {
  tables: DbTable[]; selection: Selection; onChange: (s: Selection) => void; database: string; onBack: () => void; onNext: () => void
}) {
  const [filter, setFilter] = useState('')
  const [expanded, setExpanded] = useState<string | null>(null)
  const visible = useMemo(() => tables.filter(t => `${t.schema}.${t.name}`.toLowerCase().includes(filter.toLowerCase())), [tables, filter])
  const allOn = visible.length > 0 && visible.every(t => (selection[key(t)] ?? []).length > 0)

  const toggleTable = (t: DbTable, on: boolean) => onChange({ ...selection, [key(t)]: on ? defaultOps(t) : [] })
  const toggleOp = (t: DbTable, op: string) => {
    const cur = selection[key(t)] ?? []
    onChange({ ...selection, [key(t)]: cur.includes(op) ? cur.filter(o => o !== op) : [...cur, op] })
  }
  const setAll = (on: boolean) => {
    const next = { ...selection }
    for (const t of visible) next[key(t)] = on ? defaultOps(t) : []
    onChange(next)
  }

  return (
    <div className="card pad col gap-16">
      <div className="row between wrap">
        <div>
          <div className="strong">Choose tables from {database}</div>
          <div className="small subtle">{tables.length} tables and views found. Choose which ones get endpoints and what each can do.</div>
        </div>
        <span className="badge accent">{selectedCount(selection)} selected</span>
      </div>
      <div className="row wrap">
        <div className="search"><Icon name="search" size={16} /><input className="input" placeholder="Filter tables…" value={filter} onChange={e => setFilter(e.target.value)} /></div>
        <Button size="sm" onClick={() => setAll(!allOn)}>{allOn ? 'Select none' : 'Select all'}</Button>
      </div>
      {tables.length === 0 ? <Alert kind="warning">This database has no tables yet.</Alert> : (
        <div className="table-wrap" style={{ maxHeight: 480 }}>
          <table className="table">
            <thead>
              <tr><th style={{ width: 36 }}></th><th>Table</th><th>Fields</th><th>Rows</th><th>Endpoints</th><th style={{ width: 36 }}></th></tr>
            </thead>
            <tbody>
              {visible.map(t => {
                const ops = selection[key(t)] ?? []
                const keys = t.columns.filter(c => c.isPrimaryKey && c.supported).length
                const unsupported = t.columns.filter(c => !c.supported).length
                const isOpen = expanded === key(t)
                return (
                  <Fragment key={key(t)}>
                    <tr>
                      <td><input type="checkbox" className="checkbox" checked={ops.length > 0} onChange={e => toggleTable(t, e.target.checked)} /></td>
                      <td>
                        <div className="strong">{t.name}</div>
                        <div className="tiny subtle row gap-6">
                          {t.schema && t.schema !== database && <span>{t.schema}</span>}
                          {t.isView && <span className="badge" style={{ height: 18 }}>View</span>}
                          {keys === 0 && !t.isView && <span className="badge warning" style={{ height: 18 }}>No ID column</span>}
                          {keys > 1 && <span className="badge" style={{ height: 18 }}>Combined ID</span>}
                          {unsupported > 0 && <span title="Some columns use types that can't be mapped and will be skipped">{unsupported} skipped</span>}
                        </div>
                      </td>
                      <td className="muted">{t.columns.length}</td>
                      <td className="muted">{t.rowCount != null ? t.rowCount.toLocaleString() : '—'}</td>
                      <td>
                        <div className="row wrap gap-4">
                          {OPS.map(o => {
                            const allowed = t.isView ? o.id === 'list' : keys === 1 || !o.needsKey && (o.id === 'list' || keys > 1)
                            const on = ops.includes(o.id)
                            return (
                              <button key={o.id} type="button" disabled={!allowed}
                                className={`chip${on ? ' route' : ''}`}
                                style={{ cursor: allowed ? 'pointer' : 'not-allowed', opacity: allowed ? 1 : .4, background: on ? 'var(--accent-soft)' : undefined, borderColor: on ? 'var(--accent)' : undefined }}
                                onClick={() => toggleOp(t, o.id)}
                              >{on && <Icon name="check" size={11} stroke={2.6} />}{o.label}</button>
                            )
                          })}
                        </div>
                      </td>
                      <td><Button size="sm" variant="ghost" iconOnly icon={isOpen ? 'chevronDown' : 'chevronRight'} onClick={() => setExpanded(isOpen ? null : key(t))} title="Show fields" /></td>
                    </tr>
                    {isOpen && (
                      <tr>
                        <td></td>
                        <td colSpan={5}>
                          <div className="row wrap gap-6">
                            {t.columns.map(c => (
                              <span key={c.name} className="chip" title={c.sqlType} style={{ opacity: c.supported ? 1 : .5 }}>
                                {c.isPrimaryKey && <Icon name="key" size={11} />}{c.name}
                                <span className="subtle">· {friendly(c.clrType)}{c.nullable ? '' : ' · required'}</span>
                              </span>
                            ))}
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <div className="row between">
        <Button icon="arrowLeft" onClick={onBack}>Back</Button>
        <Button variant="primary" icon="arrowRight" disabled={selectedCount(selection) === 0} onClick={onNext}>Continue</Button>
      </div>
    </div>
  )
}

function ResultView({ result }: { result: GenerateProjectResult }) {
  const ok = result.build?.success !== false
  return (
    <div className="page" style={{ maxWidth: 760 }}>
      <div className="card pad" style={{ padding: 32, textAlign: 'center' }}>
        <div className="success-burst" style={ok ? undefined : { background: 'var(--warning-soft)', color: 'var(--warning)' }}>
          <Icon name={ok ? 'check' : 'alert'} size={34} stroke={2.5} />
        </div>
        <h1 className="page-title">{ok ? 'Your API is ready!' : 'Project created, with a few problems'}</h1>
        <p className="muted mt-8">
          {ok
            ? 'The project was created and it builds without errors. Open it to explore the endpoints, run it and try them out.'
            : 'The project was created, but the build check found problems. Open it to see the details.'}
        </p>
        <div className="row mt-24" style={{ justifyContent: 'center' }}>
          <Button variant="primary" size="lg" icon="arrowRight" onClick={() => navigate({ page: 'project', path: result.projectPath })}>Open the project</Button>
          <Button size="lg" icon="folder" onClick={() => api.reveal(result.projectPath)}>Show in folder</Button>
        </div>
        <div className="divider" />
        <div className="row wrap" style={{ justifyContent: 'center' }}>
          <span className="badge accent">{result.createdFiles.length} files created</span>
          {result.build && <span className={`badge ${result.build.success ? 'success' : 'warning'}`}>{result.build.success ? 'Build passed' : `${result.build.errorCount} build problem${result.build.errorCount > 1 ? 's' : ''}`}</span>}
        </div>
        {result.build && !result.build.success && (
          <div className="col mt-16" style={{ textAlign: 'left' }}>
            {result.build.errors.slice(0, 5).map((e, i) => <Alert key={i} kind="warning">{e.file ? `${e.file.split('/').pop()}: ` : ''}{e.message}</Alert>)}
          </div>
        )}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- helpers

const key = (t: DbTable) => `${t.schema}.${t.name}`

function defaultOps(t: DbTable): string[] {
  if (t.isView) return ['list']
  const keys = t.columns.filter(c => c.isPrimaryKey && c.supported).length
  if (keys === 1) return ['list', 'get', 'create', 'update', 'delete']
  if (keys > 1) return ['list', 'create']
  return ['list']
}

function selectedCount(s: Selection) { return Object.values(s).filter(o => o.length > 0).length }

function toProjectName(db: string) {
  const p = db.split(/[^A-Za-z0-9]+/).filter(Boolean).map(w => w[0].toUpperCase() + w.slice(1)).join('')
  return /^[A-Za-z]/.test(p) ? p : 'My' + p
}

function shortPath(p: string) {
  const parts = p.split(/[\\/]/).filter(Boolean)
  return parts.slice(-2).join('\\')
}

function friendly(t: string) {
  switch (t) {
    case 'string': return 'text'
    case 'int': case 'long': case 'short': case 'byte': case 'uint': case 'ulong': case 'sbyte': case 'ushort': return 'number'
    case 'decimal': case 'double': case 'float': return 'decimal'
    case 'bool': return 'yes/no'
    case 'DateTime': case 'DateTimeOffset': return 'date & time'
    case 'DateOnly': return 'date'
    case 'TimeOnly': case 'TimeSpan': return 'time'
    case 'Guid': return 'unique ID'
    case 'byte[]': return 'file data'
    case '': return 'not supported'
    default: return t
  }
}

