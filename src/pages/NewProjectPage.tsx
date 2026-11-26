import { Fragment, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { FolderPicker } from '../components/FolderPicker'
import { Icon } from '../components/Icon'
import { Alert, Button, Choice, Field, Spinner, Switch, errorText } from '../components/ui'
import { href, navigate } from '../router'
import type { DbConnectionInfo, DbProvider, DbTable, EndpointStyle, GenerateProjectResult, TargetOption } from '../types'

const OPS = [
  { id: 'list', label: 'List', needsKey: false },
  { id: 'get', label: 'Get one', needsKey: true },
  { id: 'create', label: 'Add', needsKey: false },
  { id: 'update', label: 'Update', needsKey: true },
  { id: 'delete', label: 'Delete', needsKey: true },
] as const

const CONN_KEY = 'es-last-connection'
const PROVIDERS: { id: DbProvider; title: string; desc: string; port: number | null; user: string }[] = [
  { id: 'SqlServer', title: 'SQL Server', desc: 'SQL Server, LocalDB or Azure SQL', port: null, user: '' },
  { id: 'PostgreSql', title: 'PostgreSQL', desc: 'PostgreSQL, Supabase, Neon and others', port: 5432, user: 'postgres' },
  { id: 'MySql', title: 'MySQL', desc: 'MySQL or MariaDB', port: 3306, user: 'root' },
  { id: 'Oracle', title: 'Oracle', desc: 'Oracle Database 12c or newer', port: 1521, user: '' },
  { id: 'Sqlite', title: 'SQLite', desc: 'A database stored in one file', port: null, user: '' },
]

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
  const [clean, setClean] = useState(true)
  const [jwt, setJwt] = useState(true)
  const [targets, setTargets] = useState<TargetOption[]>([])
  const [tfm, setTfm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [picker, setPicker] = useState(false)
  const [filePicker, setFilePicker] = useState(false)
  const isOracle = conn.provider === 'Oracle'
  const isSqlite = conn.provider === 'Sqlite'
  const steps = ['Connect', isOracle ? 'Schema' : 'Database', 'Tables', 'Settings']
  const [result, setResult] = useState<GenerateProjectResult | null>(null)
  const [generating, setGenerating] = useState(false)

  useEffect(() => {
    api.system().then(s => {
      setLocation(l => l || s.defaultLocation)
      setTargets(s.targets)
      setTfm(t => t || (s.targets.find(x => x.recommended) ?? s.targets[0])?.tfm || 'net10.0')
    }).catch(() => {})
  }, [])
  const target = targets.find(t => t.tfm === tfm)

  const set = (patch: Partial<DbConnectionInfo>) => setConn(c => ({ ...c, ...patch }))

  const connect = async () => {
    setBusy(true); setError(null)
    try {
      const r = await api.dbConnect(conn)
      setDatabases(r.databases)
      setServerVersion(r.version)
      try { localStorage.setItem(CONN_KEY, JSON.stringify({ ...conn, password: '', database: null })) } catch { /* ignore */ }
      if (conn.provider === 'Sqlite') await chooseDb(r.databases[0] ?? 'main') // a SQLite file is a single database
      else setStep(1)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  const pickProvider = (id: DbProvider) => {
    const info = PROVIDERS.find(x => x.id === id)!
    if (id === conn.provider) return
    set({
      provider: id,
      port: info.port,
      integratedSecurity: id === 'SqlServer',
      username: info.user || (id === 'SqlServer' ? '' : conn.username ?? ''),
      server: id === 'Sqlite' ? '' : conn.provider === 'Sqlite' || !conn.server ? 'localhost' : conn.server,
      serviceName: id === 'Oracle' ? conn.serviceName ?? '' : null,
    })
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
        targetFramework: tfm || 'net10.0',
        projectName: projectName.trim(),
        location,
        style,
        includeApiDocs: docs,
        cleanArchitecture: clean,
        jwt: clean && jwt,
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
      <p className="muted" style={{ marginBottom: 22 }}>Pick the tables you want and Stackwright creates a complete, ready-to-run ASP.NET Core project with endpoints to read and change your data.</p>

      <div className="stepper">
        {steps.map((s, i) => (
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
            <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))' }}>
              {PROVIDERS.map(pv => <Choice key={pv.id} icon="database" title={pv.title} desc={pv.desc} selected={conn.provider === pv.id} onClick={() => pickProvider(pv.id)} />)}
            </div>
          </Field>
          {isSqlite ? (
            <Field label="Database file" hint="The .db, .sqlite or .sqlite3 file. The generated API will use this file.">
              <div className="row gap-6">
                <input className="input grow" value={conn.server} onChange={e => set({ server: e.target.value })} placeholder="C:\data\app.db" />
                <Button icon="folder" onClick={() => setFilePicker(true)}>Browse</Button>
              </div>
            </Field>
          ) : (
            <>
              <div className="grid-2">
                <Field label={isOracle ? 'Host' : 'Server'} hint={conn.provider === 'SqlServer' ? 'e.g. localhost, .\\SQLEXPRESS or (localdb)\\MSSQLLocalDB' : 'e.g. localhost or db.company.com'}>
                  <input className="input" value={conn.server} onChange={e => set({ server: e.target.value })} />
                </Field>
                <Field label="Port" hint={conn.provider === 'SqlServer' ? 'Leave empty for the default' : `Default is ${PROVIDERS.find(x => x.id === conn.provider)?.port}`}>
                  <input className="input" inputMode="numeric" value={conn.port ?? ''} onChange={e => set({ port: e.target.value ? Number(e.target.value.replace(/\D/g, '')) || null : null })} placeholder={String(PROVIDERS.find(x => x.id === conn.provider)?.port ?? 1433)} />
                </Field>
              </div>
              {isOracle && (
                <Field label="Service name" hint="The database service, for example ORCLPDB1, XEPDB1 or FREEPDB1. Ask your DBA if you're not sure.">
                  <input className="input" value={conn.serviceName ?? ''} onChange={e => set({ serviceName: e.target.value })} placeholder="ORCLPDB1" />
                </Field>
              )}
              {conn.provider === 'SqlServer' && (
                <Switch checked={conn.integratedSecurity} onChange={v => set({ integratedSecurity: v })} label="Sign in with my Windows account" />
              )}
              {!conn.integratedSecurity && (
                <div className="grid-2">
                  <Field label="Username"><input className="input" value={conn.username ?? ''} onChange={e => set({ username: e.target.value })} autoComplete="username" /></Field>
                  <Field label="Password"><input className="input" type="password" value={conn.password ?? ''} onChange={e => set({ password: e.target.value })} autoComplete="current-password" /></Field>
                </div>
              )}
            </>
          )}
          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <Button variant="primary" icon="zap" loading={busy} onClick={connect} disabled={!conn.server.trim() || (isOracle && !conn.serviceName?.trim())}>{isSqlite ? 'Open database' : 'Connect'}</Button>
          </div>
        </div>
      )}

      {step === 1 && (
        <DatabaseStep databases={databases} version={serverVersion} busy={busy} onBack={() => setStep(0)} onPick={chooseDb} oracle={isOracle} />
      )}

      {step === 2 && (
        <TablesStep tables={tables} selection={selection} onChange={setSelection} database={conn.database!} onBack={() => setStep(isSqlite ? 0 : 1)} onNext={() => setStep(3)} />
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
          <Field label=".NET version" hint="Pick the version your team uses. Each one gets the matching database and documentation packages.">
            {targets.length === 0 ? <div className="row small subtle"><Spinner /> Checking which .NET versions are installed…</div> : (
              <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' }}>
                {targets.map(t => (
                  <button key={t.tfm} type="button" className={`choice${tfm === t.tfm ? ' selected' : ''}`} onClick={() => setTfm(t.tfm)} style={{ flexDirection: 'column', gap: 6 }}>
                    <span className="row between" style={{ width: '100%' }}>
                      <span className="choice-title" style={{ fontSize: 15 }}>{t.label}</span>
                      {t.recommended && <span className="badge success" style={{ height: 20 }}>Recommended</span>}
                    </span>
                    <span className="choice-desc">{t.support}</span>
                    {!t.canRun && <span className="choice-desc" style={{ color: 'var(--warning)' }}>Runtime not installed: can build, can’t run here</span>}
                  </button>
                ))}
              </div>
            )}
          </Field>
          <Field label="Structure">
            <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' }}>
              <Choice icon="layers" title="Clean Architecture (recommended)" desc="A solution with Domain, Application, Infrastructure and Api projects. Business rules stay separate from the database and HTTP, so it grows well." selected={clean} onClick={() => setClean(true)} />
              <Choice icon="box" title="Single project" desc="Everything in one small project. Good for prototypes and tiny services." selected={!clean} onClick={() => setClean(false)} />
            </div>
          </Field>
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
              <div className="small subtle">Adds a docs page to your API where every endpoint can be explored and tried in the browser ({target && target.major < 9 ? 'Swagger UI' : 'Scalar'}).</div>
            </div>
            <Switch checked={docs} onChange={setDocs} />
          </div>
          <div className="card pad row" style={{ background: 'var(--bg-sunken)', opacity: clean ? 1 : .6 }}>
            <div className="group-icon"><Icon name="lock" size={16} /></div>
            <div className="grow">
              <div className="strong">Sign-in with JWT</div>
              <div className="small subtle">{clean ? 'Every endpoint requires a token from POST /api/auth/login. An admin account is created; its password is shown once when the project is ready.' : 'Available with Clean Architecture.'}</div>
            </div>
            <Switch checked={clean && jwt} onChange={setJwt} disabled={!clean} />
          </div>
          <Alert kind="info">
            A <strong>{target?.label ?? '.NET'}</strong> {clean ? 'solution' : 'project'} with {selectedCount(selection)} table{selectedCount(selection) === 1 ? '' : 's'} and {Object.values(selection).reduce((n, o) => n + o.length, 0)} endpoints will be created from <strong>{conn.database}</strong>.
            {!conn.integratedSecurity && ' The database password will be stored in the project’s settings file.'}
          </Alert>
          <div className="row between">
            <Button icon="arrowLeft" onClick={() => setStep(2)}>Back</Button>
            <Button variant="primary" size="lg" icon="rocket" onClick={generate} disabled={!projectName || !/^[A-Za-z]/.test(projectName) || !location.trim()}>Create project</Button>
          </div>
        </div>
      )}

      {filePicker && <FolderPicker mode="file" extensions=".db,.sqlite,.sqlite3,.db3" onPick={p => { set({ server: p }); setFilePicker(false) }} onClose={() => setFilePicker(false)} />}
      {picker && <FolderPicker mode="folder" initialPath={location || null} onPick={p => { setLocation(p); setPicker(false) }} onClose={() => setPicker(false)} />}
    </div>
  )
}

function DatabaseStep({ databases, version, busy, onBack, onPick, oracle }: { databases: string[]; version: string; busy: boolean; onBack: () => void; onPick: (db: string) => void; oracle: boolean }) {
  const [filter, setFilter] = useState('')
  const list = databases.filter(d => d.toLowerCase().includes(filter.toLowerCase()))
  return (
    <div className="card pad col gap-16">
      <div className="row between wrap">
        <div>
          <div className="strong">{oracle ? 'Choose a schema' : 'Choose a database'}</div>
          <div className="small subtle">Connected · server version {version}</div>
        </div>
        {busy && <Spinner />}
      </div>
      {databases.length > 6 && <input className="input" placeholder="Filter databases…" value={filter} onChange={e => setFilter(e.target.value)} autoFocus />}
      {databases.length === 0 ? (
        <Alert kind="warning">{oracle ? 'No schemas with tables were found for this login.' : 'No databases were found on this server, or this login can’t see any.'}</Alert>
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
        {result.adminPassword && <SignInCard user={result.adminUser ?? 'admin'} password={result.adminPassword} />}
        <div className="row mt-24 wrap" style={{ justifyContent: 'center' }}>
          <Button variant="primary" size="lg" icon="arrowRight" onClick={() => navigate({ page: 'project', path: result.projectPath })}>Open the API project</Button>
          {result.solutionPath && <Button size="lg" icon="layers" onClick={() => navigate({ page: 'solution', path: result.solutionPath! })}>Open the solution</Button>}
          <Button size="lg" icon="folder" onClick={() => api.reveal(result.solutionPath ?? result.projectPath)}>Show in folder</Button>
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

function SignInCard({ user, password }: { user: string; password: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className="card pad col gap-8 mt-16" style={{ textAlign: 'left', background: 'var(--warning-soft)', borderColor: 'color-mix(in srgb, var(--warning) 40%, var(--border))' }}>
      <div className="row gap-8"><Icon name="key" size={16} /><span className="strong grow">Admin sign-in: save this password now</span></div>
      <div className="small">It’s stored only as a hash in the project, so it can’t be shown again. Use it with <span className="mono">POST /api/auth/login</span>.</div>
      <div className="row gap-8 wrap">
        <span className="badge">User</span><span className="mono strong">{user}</span>
        <span className="badge">Password</span><span className="mono strong">{password}</span>
        <Button size="sm" icon={copied ? 'check' : 'copy'} onClick={() => { navigator.clipboard?.writeText(password); setCopied(true) }}>{copied ? 'Copied' : 'Copy'}</Button>
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

