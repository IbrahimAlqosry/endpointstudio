import { useEffect, useState } from 'react'
import { api } from '../../api'
import type { DbExtras, DbRef, DbSchema, DbsGrant, DbsTable, DbsView, DbsRoutine, DdlChange, RoutineDetails, SqlResult } from '../../types'
import { Icon } from '../Icon'
import { Alert, Button, Empty, Field, Modal, Spinner, Switch, errorText, useConfirm, useToast } from '../ui'
import { SqlEditor } from './SqlEditor'
import { ChangeDialog } from './Structure'

type Provider = DbSchema['provider']
const q = (p: Provider, n: string) => p === 'SqlServer' ? `[${n}]` : p === 'MySql' ? `\`${n}\`` : /^[a-z_][a-z0-9_]*$/.test(n) || (p === 'Oracle' && /^[A-Z_][A-Z0-9_$#]*$/.test(n)) ? n : `"${n}"`
const full = (p: Provider, schema: string, name: string, withSchema: boolean) => (withSchema && schema ? q(p, schema) + '.' : '') + q(p, name)
const defaultSchema = (s: DbSchema) => s.provider === 'SqlServer' ? 'dbo' : s.provider === 'PostgreSql' ? 'public' : s.provider === 'Oracle' ? s.database : ''

// ---------------------------------------------------------------- templates

export type CodeKind = 'procedure' | 'function' | 'trigger'

export function template(s: DbSchema, kind: CodeKind, table?: DbsTable): string {
  const p = s.provider
  const sch = table?.schema ?? defaultSchema(s)
  const tbl = table ? full(p, table.schema, table.name, s.capabilities.schemas) : full(p, sch, s.tables[0]?.name ?? 'my_table', s.capabilities.schemas)
  const key = table?.primaryKey[0] ?? s.tables[0]?.primaryKey[0] ?? 'id'
  const tName = (table?.name ?? 'table').replace(/[^A-Za-z0-9_]/g, '_')
  switch (p) {
    case 'SqlServer':
      if (kind === 'procedure') return `CREATE PROCEDURE [${sch}].[NewProcedure]\n    @Id int\nAS\nBEGIN\n    SET NOCOUNT ON;\n\n    SELECT * FROM ${tbl} WHERE ${q(p, key)} = @Id;\nEND`
      if (kind === 'function') return `CREATE FUNCTION [${sch}].[NewFunction] (@Value int)\nRETURNS int\nAS\nBEGIN\n    RETURN @Value * 2;\nEND`
      return `CREATE TRIGGER [${sch}].[trg_${tName}_Changed]\nON ${tbl}\nAFTER INSERT, UPDATE\nAS\nBEGIN\n    SET NOCOUNT ON;\n    -- "inserted" holds the new rows, "deleted" the old ones\nEND`
    case 'PostgreSql':
      if (kind === 'procedure') return `CREATE OR REPLACE PROCEDURE ${sch}.new_procedure(p_id integer)\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    -- your statements, for example:\n    -- UPDATE ${tbl} SET ... WHERE ${q(p, key)} = p_id;\nEND;\n$$;`
      if (kind === 'function') return `CREATE OR REPLACE FUNCTION ${sch}.new_function(p_value integer)\nRETURNS integer\nLANGUAGE sql\nAS $$\n    SELECT p_value * 2;\n$$;`
      return `CREATE OR REPLACE FUNCTION ${sch}.trg_${tName.toLowerCase()}_fn()\nRETURNS trigger\nLANGUAGE plpgsql\nAS $$\nBEGIN\n    -- NEW holds the new row, OLD the previous one\n    RETURN NEW;\nEND;\n$$;\n\nCREATE TRIGGER trg_${tName.toLowerCase()}\nBEFORE INSERT OR UPDATE ON ${tbl}\nFOR EACH ROW EXECUTE FUNCTION ${sch}.trg_${tName.toLowerCase()}_fn();`
    case 'MySql':
      if (kind === 'procedure') return `CREATE PROCEDURE new_procedure(IN p_id INT)\nBEGIN\n    SELECT * FROM ${tbl} WHERE ${q(p, key)} = p_id;\nEND`
      if (kind === 'function') return `CREATE FUNCTION new_function(p_value INT)\nRETURNS INT\nDETERMINISTIC\nBEGIN\n    RETURN p_value * 2;\nEND`
      return `CREATE TRIGGER trg_${tName}_before_insert\nBEFORE INSERT ON ${tbl}\nFOR EACH ROW\nBEGIN\n    -- NEW holds the new row, for example: SET NEW.created_at = NOW();\n    SET @last_insert_check = 1;\nEND`
    case 'Oracle':
      if (kind === 'procedure') return `CREATE OR REPLACE PROCEDURE NEW_PROCEDURE (P_ID IN NUMBER) AS\nBEGIN\n    NULL; -- your statements\nEND;`
      if (kind === 'function') return `CREATE OR REPLACE FUNCTION NEW_FUNCTION (P_VALUE IN NUMBER) RETURN NUMBER AS\nBEGIN\n    RETURN P_VALUE * 2;\nEND;`
      return `CREATE OR REPLACE TRIGGER TRG_${tName.toUpperCase()}_BI\nBEFORE INSERT ON ${tbl}\nFOR EACH ROW\nBEGIN\n    NULL; -- :NEW holds the new row\nEND;`
    default:
      return `CREATE TRIGGER trg_${tName}_after_insert\nAFTER INSERT ON ${tbl}\nBEGIN\n    SELECT 1; -- NEW holds the new row\nEND`
  }
}

/** Runs CREATE / ALTER code. MySQL and SQLite statements can't be split at ";" so they go through as one statement. */
async function runCode(dbRef: DbRef, schema: DbSchema, sql: string, replace?: { kind: string; name: string }) {
  if (replace && schema.provider === 'MySql') return api.dbsApply(dbRef, { kind: 'replaceRoutine', table: replace.name, oldName: replace.kind, sql })
  if (schema.provider === 'MySql' || schema.provider === 'Sqlite') return api.dbsApply(dbRef, { kind: 'script', sql })
  const r = await api.dbsSql(dbRef, sql, false, true)
  return r
}

export function CodeEditorDialog({ dbRef, schema, title, initial, replace, before, onClose, onDone }: {
  dbRef: DbRef; schema: DbSchema; title: string; initial: string
  replace?: { kind: string; name: string }
  /** A change to make first (e.g. delete the old trigger when it can't be replaced in place). */
  before?: DdlChange
  onClose: () => void; onDone: () => void
}) {
  const toast = useToast()
  const [sql, setSql] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const save = async () => {
    setBusy(true); setError(null)
    try {
      if (before) await api.dbsApply(dbRef, before)
      await runCode(dbRef, schema, sql, replace)
      toast.success('Saved to the database.')
      onDone()
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }
  return (
    <Modal title={title} icon="code" size="wide" onClose={onClose}
      footer={<><span className="tiny subtle grow" style={{ textAlign: 'left' }}>Ctrl+Space suggests tables and columns · Ctrl+Enter saves</span><Button onClick={onClose}>Cancel</Button><Button variant="primary" icon="check" loading={busy} onClick={save}>Save</Button></>}>
      <div className="col gap-10">
        {error && <Alert kind="error">{error}</Alert>}
        <SqlEditor value={sql} onChange={setSql} schema={schema} rows={18} onRun={save} autoFocus />
      </div>
    </Modal>
  )
}

// ---------------------------------------------------------------- results

export function ResultSets({ result }: { result: SqlResult }) {
  return (
    <div className="col gap-10">
      {result.results.map((set, i) => set.columns.length === 0
        ? <div key={i} className="card pad row gap-8"><Icon name="checkCircle" size={18} /><span>{set.message ?? 'Done'}{set.affected != null && set.affected >= 0 ? ` · ${set.affected} row${set.affected === 1 ? '' : 's'} affected` : ''}</span></div>
        : (
          <div key={i} className="table-wrap grid-wrap">
            <table className="table data-grid">
              <thead><tr>{set.columns.map((c, j) => <th key={j}>{c || '(no name)'}</th>)}</tr></thead>
              <tbody>{set.rows.map((r, j) => <tr key={j}>{r.map((v, k) => <td key={k} className={v === null ? 'null' : ''}><span className="cell">{v ?? 'empty'}</span></td>)}</tr>)}</tbody>
            </table>
            {set.rows.length === 0 && <div className="empty small subtle">No rows.</div>}
          </div>
        ))}
      <div className="tiny subtle">{result.durationMs} ms</div>
    </div>
  )
}

// ---------------------------------------------------------------- procedures & functions

export function RoutinePanel({ dbRef, schema, routine, onChanged }: { dbRef: DbRef; schema: DbSchema; routine: DbsRoutine; onChanged: () => void }) {
  const toast = useToast()
  const [d, setD] = useState<RoutineDetails | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [values, setValues] = useState<Record<string, string>>({})
  const [result, setResult] = useState<SqlResult | null>(null)
  const [running, setRunning] = useState(false)
  const [editing, setEditing] = useState(false)
  const load = () => api.dbsRoutineDetails(dbRef, routine.schema, routine.name, routine.kind).then(setD).catch(e => setError(errorText(e)))
  useEffect(() => { setD(null); setResult(null); load() }, [routine.schema, routine.name])
  const inputs = d?.parameters.filter(p => p.mode.toUpperCase().includes('IN')) ?? []
  const run = async () => {
    setRunning(true); setResult(null)
    try { setResult(await api.dbsRunRoutine(dbRef, routine.schema, routine.name, routine.kind, values)) }
    catch (e) { toast.error(errorText(e)) } finally { setRunning(false) }
  }
  if (error) return <Alert kind="error">{error}</Alert>
  if (!d) return <div className="row small subtle"><Spinner /> Loading…</div>
  return (
    <div className="col gap-16">
      {routine.kind !== 'Package' && (
        <div className="card pad col gap-12" style={{ background: 'var(--bg-sunken)' }}>
          <div className="row gap-8"><Icon name="play" size={16} /><span className="strong grow">Run it</span>{d.returns && <span className="badge">returns {d.returns}</span>}</div>
          {inputs.length === 0 ? <div className="small subtle">It takes no values.</div> : (
            <div className="grid-2">
              {inputs.map(p => (
                <Field key={p.name} label={<span className="row gap-4">{p.name.replace(/^@/, '')}<span className="tiny subtle" style={{ fontWeight: 400 }}>{p.type}</span></span>}>
                  <input className="input" value={values[p.name] ?? ''} placeholder="empty" onChange={e => setValues(v => ({ ...v, [p.name]: e.target.value }))} onKeyDown={e => { if (e.key === 'Enter') run() }} />
                </Field>
              ))}
            </div>
          )}
          <div><Button variant="primary" icon="play" loading={running} onClick={run}>Run</Button></div>
          {result && <ResultSets result={result} />}
        </div>
      )}
      <div className="col gap-8">
        <div className="row gap-6">
          <span className="strong grow">Definition</span>
          {routine.kind !== 'Package' && !d.editable.startsWith('(') && <Button size="sm" icon="edit" onClick={() => setEditing(true)}>Edit</Button>}
          <Button size="sm" variant="ghost" icon="copy" onClick={() => { navigator.clipboard?.writeText(d.definition); toast.info('Copied.') }}>Copy</Button>
        </div>
        <pre className="log" style={{ whiteSpace: 'pre-wrap', maxHeight: 480 }}>{d.definition}</pre>
      </div>
      {editing && <CodeEditorDialog dbRef={dbRef} schema={schema} title={`Edit ${routine.name}`} initial={d.editable} replace={{ kind: routine.kind.toUpperCase(), name: routine.name }}
        onClose={() => setEditing(false)} onDone={() => { setEditing(false); load(); onChanged() }} />}
    </div>
  )
}

// ---------------------------------------------------------------- views

export function ViewDefinition({ dbRef, schema, view, onChanged, onRenamed }: { dbRef: DbRef; schema: DbSchema; view: DbsView; onChanged: () => void; onRenamed: (name: string) => void }) {
  const [editing, setEditing] = useState(false)
  const [sql, setSql] = useState('')
  const [change, setChange] = useState<{ change: DdlChange; title: string; after?: () => void } | null>(null)
  const [renaming, setRenaming] = useState<string | null>(null)
  const body = (view.definition ?? '').replace(/^[\s\S]*?\bAS\b\s*/i, m => /CREATE\s/i.test(m) ? '' : m).trim()
  return (
    <div className="col gap-10">
      <div className="row gap-6">
        <span className="small subtle grow">The query this view shows.</span>
        <Button size="sm" icon="edit" onClick={() => { setSql(/^\s*CREATE\s/i.test(view.definition ?? '') ? body : (view.definition ?? '').trim()); setEditing(true) }}>Change the query</Button>
        {schema.provider !== 'Sqlite' && <Button size="sm" variant="ghost" onClick={() => setRenaming(view.name)}>Rename</Button>}
      </div>
      {!editing && <pre className="log" style={{ whiteSpace: 'pre-wrap', maxHeight: 520 }}>{view.definition ?? 'The definition isn’t available.'}</pre>}
      {editing && (
        <>
          <SqlEditor value={sql} onChange={setSql} schema={schema} rows={12} autoFocus />
          <div className="row gap-6">
            <Button variant="primary" icon="check" onClick={() => setChange({ change: { kind: 'alterView', schema: view.schema, table: view.name, sql }, title: `Change the view ${view.name}`, after: () => setEditing(false) })}>Save</Button>
            <Button onClick={() => setEditing(false)}>Cancel</Button>
          </div>
        </>
      )}
      {renaming !== null && (
        <Modal title={`Rename ${view.name}`} icon="edit" size="narrow" onClose={() => setRenaming(null)}
          footer={<><Button onClick={() => setRenaming(null)}>Cancel</Button><Button variant="primary" disabled={!renaming.trim() || renaming === view.name} onClick={() => { const n = renaming.trim(); setRenaming(null); setChange({ change: { kind: 'renameView', schema: view.schema, table: view.name, newName: n }, title: `Rename ${view.name} to ${n}`, after: () => onRenamed(n) }) }}>Continue</Button></>}>
          <Field label="New name"><input className="input" autoFocus value={renaming} onChange={e => setRenaming(e.target.value.replace(/\s/g, '_'))} /></Field>
        </Modal>
      )}
      {change && <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)} onDone={() => { const a = change.after; setChange(null); onChanged(); a?.() }} />}
    </div>
  )
}

// ---------------------------------------------------------------- triggers of a table

export function TriggersPanel({ dbRef, schema, extras, table, onChanged }: { dbRef: DbRef; schema: DbSchema; extras: DbExtras | null; table: DbsTable; onChanged: () => void }) {
  const [open, setOpen] = useState<string | null>(null)
  const [defs, setDefs] = useState<Record<string, string>>({})
  const [editor, setEditor] = useState<{ title: string; sql: string; before?: DdlChange; replace?: { kind: string; name: string } } | null>(null)
  const [change, setChange] = useState<{ change: DdlChange; title: string } | null>(null)
  const p = schema.provider
  const list = extras?.triggers.filter(t => t.table === table.name && t.schema === table.schema) ?? []
  const show = (name: string) => {
    setOpen(open === name ? null : name)
    if (!defs[name]) api.dbsTrigger(dbRef, table.schema, table.name, name).then(t => setDefs(d => ({ ...d, [name]: t }))).catch(() => {})
  }
  const edit = async (name: string) => {
    const text = defs[name] ?? await api.dbsTrigger(dbRef, table.schema, table.name, name)
    if (p === 'SqlServer') setEditor({ title: `Edit ${name}`, sql: text.replace(/^\s*CREATE\s+TRIGGER/i, 'CREATE OR ALTER TRIGGER') })
    else if (p === 'Oracle') setEditor({ title: `Edit ${name}`, sql: text })
    else if (p === 'MySql') setEditor({ title: `Edit ${name}`, sql: text.replace(/^\s*CREATE\s+DEFINER\s*=\s*\S+\s+/i, 'CREATE '), replace: { kind: 'TRIGGER', name } })
    else if (p === 'PostgreSql') setEditor({ title: `Edit ${name}`, sql: `DROP TRIGGER IF EXISTS ${q(p, name)} ON ${full(p, table.schema, table.name, true)};\n\n` + text.replace(/\n\n-- Function it runs:\n/, '\n\n') })
    else setEditor({ title: `Edit ${name}`, sql: text, before: { kind: 'dropTrigger', schema: table.schema, table: table.name, constraintName: name } })
  }
  return (
    <div className="col gap-10">
      <div className="row gap-6">
        <span className="small subtle grow">Triggers run automatically when rows of {table.name} are added, changed or deleted.</span>
        <Button size="sm" variant="primary" icon="plus" onClick={() => setEditor({ title: `New trigger on ${table.name}`, sql: template(schema, 'trigger', table) })}>New trigger</Button>
      </div>
      {!extras && <div className="row small subtle"><Spinner /> Loading…</div>}
      {extras && list.length === 0 && <div className="card"><Empty icon="zap" title="No triggers on this table" /></div>}
      {list.map(t => (
        <div key={t.name} className="card">
          <div className="git-branch">
            <Icon name="zap" size={15} />
            <div className="grow">
              <div className="row gap-6"><span className="strong">{t.name}</span>{!t.enabled && <span className="badge warning">switched off</span>}</div>
              <div className="tiny subtle">{[t.timing, t.events].filter(Boolean).join(' · ') || 'Runs on changes'}</div>
            </div>
            <Button size="sm" variant="ghost" onClick={() => show(t.name)}>{open === t.name ? 'Hide' : 'Show'} definition</Button>
            <Button size="sm" variant="ghost" icon="edit" onClick={() => edit(t.name)}>Edit</Button>
            {p !== 'MySql' && p !== 'Sqlite' && <Button size="sm" variant="ghost" onClick={() => setChange({ change: { kind: 'toggleTrigger', schema: table.schema, table: table.name, constraintName: t.name, enabled: !t.enabled }, title: `${t.enabled ? 'Switch off' : 'Switch on'} ${t.name}` })}>{t.enabled ? 'Switch off' : 'Switch on'}</Button>}
            <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete" onClick={() => setChange({ change: { kind: 'dropTrigger', schema: table.schema, table: table.name, constraintName: t.name }, title: `Delete the trigger ${t.name}?` })} />
          </div>
          {open === t.name && <pre className="log" style={{ margin: '0 14px 14px', whiteSpace: 'pre-wrap', maxHeight: 400 }}>{defs[t.name] ?? 'Loading…'}</pre>}
        </div>
      ))}
      {editor && <CodeEditorDialog dbRef={dbRef} schema={schema} title={editor.title} initial={editor.sql} before={editor.before} replace={editor.replace} onClose={() => setEditor(null)} onDone={() => { setEditor(null); setDefs({}); onChanged() }} />}
      {change && <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)} onDone={() => { setChange(null); onChanged() }} />}
    </div>
  )
}

// ---------------------------------------------------------------- rules, copy and upkeep for a table

export function TableExtras({ dbRef, schema, extras, table, onChanged, onOpenTable }: { dbRef: DbRef; schema: DbSchema; extras: DbExtras | null; table: DbsTable; onChanged: () => void; onOpenTable: (key: string) => void }) {
  const [change, setChange] = useState<{ change: DdlChange; title: string; after?: () => void } | null>(null)
  const [unique, setUnique] = useState<string[] | null>(null)
  const [check, setCheck] = useState<string | null>(null)
  const [copy, setCopy] = useState<{ name: string; withData: boolean } | null>(null)
  const rules = extras?.constraints.filter(c => c.table === table.name && c.schema === table.schema) ?? []
  const base = { schema: table.schema, table: table.name }
  const canRules = schema.provider !== 'Sqlite'
  return (
    <div className="col gap-20">
      <section className="col gap-8">
        <div className="row"><h3 className="section-title grow">Rules <span className="badge">{rules.length}</span></h3>
          {canRules && <Button size="sm" icon="plus" onClick={() => setUnique([])}>Unique values</Button>}
          {canRules && extras?.capabilities.checkConstraints && <Button size="sm" icon="plus" onClick={() => setCheck('')} style={{ marginLeft: 6 }}>Check rule</Button>}
        </div>
        {rules.length === 0 && <div className="small subtle">No extra rules. {canRules ? 'Add one to keep values unique or within limits.' : 'SQLite only accepts rules when a table is created.'}</div>}
        {rules.map(r => (
          <div key={r.name} className="relation">
            <span className={`badge ${r.kind === 'unique' ? 'warning' : 'accent'}`}>{r.kind === 'unique' ? 'unique' : 'check'}</span>
            <span className="strong">{r.kind === 'unique' ? r.columns.join(', ') : r.definition}</span>
            <div className="grow" />
            <span className="tiny subtle">{r.name}</span>
            {canRules && <Button size="sm" variant="ghost" iconOnly icon="trash" title="Remove rule" onClick={() => setChange({ change: { kind: 'dropConstraint', ...base, constraintName: r.name, unique: r.kind === 'unique' }, title: `Remove the rule ${r.name}?` })} />}
          </div>
        ))}
      </section>

      <section className="col gap-8">
        <h3 className="section-title">More</h3>
        <div className="row wrap gap-6">
          <Button size="sm" icon="copy" onClick={() => setCopy({ name: table.name + '_copy', withData: true })}>Copy table</Button>
          <Button size="sm" icon="refresh" onClick={() => setChange({ change: { kind: 'maintenance', ...base, sql: 'analyze' }, title: `Update statistics for ${table.name}` })}>Update statistics</Button>
          <Button size="sm" icon="hammer" onClick={() => setChange({ change: { kind: 'maintenance', ...base, sql: 'rebuild' }, title: `Rebuild the indexes of ${table.name}` })}>Rebuild indexes</Button>
          {(schema.provider === 'PostgreSql' || schema.provider === 'MySql') && <Button size="sm" icon="zap" onClick={() => setChange({ change: { kind: 'maintenance', ...base, sql: 'vacuum' }, title: `Clean up ${table.name}` })}>Reclaim space</Button>}
        </div>
      </section>

      {unique && (
        <Modal title="Values must be unique" subtitle={`No two rows of ${table.name} may have the same values in these columns.`} icon="key" size="narrow" onClose={() => setUnique(null)}
          footer={<><Button onClick={() => setUnique(null)}>Cancel</Button><Button variant="primary" disabled={unique.length === 0} onClick={() => { const cols = unique; setUnique(null); setChange({ change: { kind: 'addUnique', ...base, keyColumns: cols, constraintName: `UQ_${table.name}_${cols.join('_')}`.slice(0, 60) }, title: 'Add the rule' }) }}>Continue</Button></>}>
          <div className="col gap-4">
            {table.columns.map(c => <label key={c.name} className="row gap-6 small"><input type="checkbox" checked={unique.includes(c.name)} onChange={e => setUnique(u => e.target.checked ? [...(u ?? []), c.name] : (u ?? []).filter(x => x !== c.name))} />{c.name}<span className="tiny subtle">{c.type}</span></label>)}
          </div>
        </Modal>
      )}
      {check !== null && (
        <Modal title="Check rule" subtitle="A condition every row must meet, e.g. price >= 0 or end_date > start_date." icon="checkCircle" onClose={() => setCheck(null)}
          footer={<><Button onClick={() => setCheck(null)}>Cancel</Button><Button variant="primary" disabled={!check.trim()} onClick={() => { const c = check; setCheck(null); setChange({ change: { kind: 'addCheck', ...base, sql: c, constraintName: `CK_${table.name}_${Date.now() % 100000}` }, title: 'Add the rule' }) }}>Continue</Button></>}>
          <SqlEditor value={check} onChange={setCheck} schema={{ ...schema, tables: [table, ...schema.tables.filter(t => t !== table)] }} rows={3} autoFocus placeholder={`${table.columns[1]?.name ?? 'column'} >= 0`} />
        </Modal>
      )}
      {copy && (
        <Modal title={`Copy ${table.name}`} icon="copy" size="narrow" onClose={() => setCopy(null)}
          footer={<><Button onClick={() => setCopy(null)}>Cancel</Button><Button variant="primary" disabled={!copy.name.trim()} onClick={() => { const c = copy; setCopy(null); setChange({ change: { kind: 'copyTable', ...base, newName: c.name.trim(), withData: c.withData }, title: `Copy ${table.name} to ${c.name}`, after: () => onOpenTable((table.schema ? table.schema + '.' : '') + c.name.trim()) }) }}>Continue</Button></>}>
          <div className="col gap-12">
            <Field label="Name of the copy"><input className="input" autoFocus value={copy.name} onChange={e => setCopy({ ...copy, name: e.target.value.replace(/\s/g, '_') })} /></Field>
            <Switch checked={copy.withData} onChange={v => setCopy({ ...copy, withData: v })} label="Copy the rows too" />
          </div>
        </Modal>
      )}
      {change && <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)} onDone={() => { const a = change.after; setChange(null); onChanged(); a?.() }} />}
    </div>
  )
}

// ---------------------------------------------------------------- permissions on a table

const PRIVILEGES = ['SELECT', 'INSERT', 'UPDATE', 'DELETE']

export function PermissionsPanel({ dbRef, schema, extras, table }: { dbRef: DbRef; schema: DbSchema; extras: DbExtras | null; table: { schema: string; name: string } }) {
  const [grants, setGrants] = useState<DbsGrant[] | null>(null)
  const [who, setWho] = useState('')
  const [privs, setPrivs] = useState<string[]>(['SELECT'])
  const [change, setChange] = useState<{ change: DdlChange; title: string } | null>(null)
  const load = () => api.dbsGrants(dbRef, table.schema, table.name).then(setGrants).catch(() => setGrants([]))
  useEffect(() => { load() }, [table.schema, table.name])
  if (schema.provider === 'Sqlite') return <div className="card"><Empty icon="lock" title="SQLite has no users or permissions">Anyone who can open the file can read and change it.</Empty></div>
  const byGrantee = new Map<string, string[]>()
  for (const g of grants ?? []) byGrantee.set(g.grantee, [...(byGrantee.get(g.grantee) ?? []), g.privilege])
  const principals = extras?.principals ?? []
  return (
    <div className="col gap-14">
      <div className="card pad col gap-10" style={{ background: 'var(--bg-sunken)' }}>
        <div className="strong">Give access to {table.name}</div>
        <div className="row wrap gap-8">
          {principals.length > 0 ? (
            <select className="select" style={{ width: 'auto', minWidth: 200 }} value={who} onChange={e => setWho(e.target.value)}>
              <option value="">Choose a user or role…</option>
              {principals.map(p => <option key={p.name} value={p.name}>{p.name}{p.kind === 'role' ? ' (role)' : ''}</option>)}
            </select>
          ) : <input className="input" style={{ width: 220 }} placeholder="User or role name" value={who} onChange={e => setWho(e.target.value)} />}
          {PRIVILEGES.map(p => <label key={p} className="row gap-4 small"><input type="checkbox" checked={privs.includes(p)} onChange={e => setPrivs(x => e.target.checked ? [...x, p] : x.filter(y => y !== p))} />{p === 'SELECT' ? 'Read' : p === 'INSERT' ? 'Add' : p === 'UPDATE' ? 'Change' : 'Delete'}</label>)}
          <Button size="sm" variant="primary" disabled={!who || privs.length === 0} onClick={() => setChange({ change: { kind: 'grant', schema: table.schema, table: table.name, principal: who, privileges: privs }, title: `Give ${who} access to ${table.name}` })}>Give access</Button>
        </div>
      </div>
      {!grants && <div className="row small subtle"><Spinner /> Loading…</div>}
      {grants && byGrantee.size === 0 && <div className="small subtle">Nobody has been given access to this table directly. Owners and administrators always have access.</div>}
      {[...byGrantee.entries()].map(([g, ps]) => (
        <div key={g} className="relation">
          <Icon name="user" size={14} /><span className="strong">{g}</span>
          {ps.map(p => <span key={p} className="badge">{p}</span>)}
          <div className="grow" />
          <Button size="sm" variant="ghost" onClick={() => setChange({ change: { kind: 'revoke', schema: table.schema, table: table.name, principal: g, privileges: ps.filter(p => PRIVILEGES.includes(p.toUpperCase())).length ? ps.filter(p => PRIVILEGES.includes(p.toUpperCase())) : ['ALL'] }, title: `Remove ${g}'s access to ${table.name}` })}>Remove access</Button>
        </div>
      ))}
      {change && <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)} onDone={() => { setChange(null); load() }} />}
    </div>
  )
}

// ---------------------------------------------------------------- server-wide: users, schemas, sequences, upkeep, backups

export function ServerPanel({ dbRef, schema, extras, onChanged }: { dbRef: DbRef; schema: DbSchema; extras: DbExtras | null; onChanged: () => void }) {
  const toast = useToast()
  const confirm = useConfirm()
  const [change, setChange] = useState<{ change: DdlChange; title: string } | null>(null)
  const [userForm, setUserForm] = useState<{ kind: 'user' | 'role'; name: string; password: string } | null>(null)
  const [memberOf, setMemberOf] = useState<{ role: string; user: string } | null>(null)
  const [seqForm, setSeqForm] = useState<{ name: string; start: string } | null>(null)
  const [restart, setRestart] = useState<{ name: string; schema: string; value: string } | null>(null)
  const [schemaName, setSchemaName] = useState<string | null>(null)
  const [folder, setFolder] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const caps = extras?.capabilities
  const p = schema.provider
  const backup = async () => {
    setBusy('backup')
    try { const r = await api.dbsBackup(dbRef, folder); toast.success(r.message) } catch (e) { toast.error(errorText(e)) } finally { setBusy(null) }
  }
  const script = async (withData: boolean) => {
    setBusy(withData ? 'data' : 'schema')
    try {
      const r = await api.dbsScriptDb(dbRef, withData)
      const a = document.createElement('a'); a.href = URL.createObjectURL(r.blob); a.download = r.name; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 2000)
    } catch (e) { toast.error(errorText(e)) } finally { setBusy(null) }
  }
  const users = extras?.principals.filter(x => x.kind === 'user') ?? []
  const roles = extras?.principals.filter(x => x.kind === 'role') ?? []

  return (
    <div className="col gap-16">
      {extras?.notes.map(n => <Alert key={n} kind="info">{n}</Alert>)}

      <div className="card pad col gap-10">
        <div className="row"><div className="group-icon"><Icon name="download" size={16} /></div><div className="grow" style={{ marginLeft: 10 }}><div className="strong">Backup & copy</div><div className="small subtle">Keep a copy of {schema.database}, or move it to another server.</div></div></div>
        <div className="row wrap gap-6">
          <Button size="sm" icon="fileText" loading={busy === 'schema'} onClick={() => script(false)}>Save design as SQL script</Button>
          <Button size="sm" icon="fileText" loading={busy === 'data'} onClick={() => script(true)}>Save design and data as SQL script</Button>
        </div>
        {caps?.backup && p !== 'Oracle' && (
          <div className="row wrap gap-6">
            <input className="input" style={{ maxWidth: 420 }} value={folder} onChange={e => setFolder(e.target.value)}
              placeholder={p === 'SqlServer' ? 'Folder on the database server (empty = its backup folder)' : p === 'Sqlite' ? 'Folder (empty = next to the database file)' : 'Folder for the backup file'} />
            <Button size="sm" variant="primary" icon="download" loading={busy === 'backup'} onClick={backup}>Back up now</Button>
          </div>
        )}
      </div>

      <div className="card pad col gap-10">
        <div className="row"><div className="group-icon"><Icon name="hammer" size={16} /></div><div className="grow" style={{ marginLeft: 10 }}><div className="strong">Upkeep</div><div className="small subtle">Keeps queries fast. Safe to run at any time; large databases take a while.</div></div></div>
        <div className="row wrap gap-6">
          <Button size="sm" icon="refresh" onClick={() => setChange({ change: { kind: 'maintenance', sql: 'analyze' }, title: 'Update statistics for every table' })}>Update statistics</Button>
          <Button size="sm" icon="hammer" onClick={() => setChange({ change: { kind: 'maintenance', sql: 'rebuild' }, title: 'Rebuild every index' })}>Rebuild indexes</Button>
          {(p === 'PostgreSql' || p === 'Sqlite' || p === 'MySql') && <Button size="sm" icon="zap" onClick={() => setChange({ change: { kind: 'maintenance', sql: 'vacuum' }, title: 'Reclaim unused space' })}>Reclaim space</Button>}
        </div>
      </div>

      {caps?.users && (
        <div className="card pad col gap-10">
          <div className="row"><div className="group-icon"><Icon name="user" size={16} /></div><div className="grow" style={{ marginLeft: 10 }}><div className="strong">Users & roles</div><div className="small subtle">Who can sign in, and groups of permissions you can give to several users at once.</div></div>
            <Button size="sm" icon="plus" onClick={() => setUserForm({ kind: 'user', name: '', password: '' })}>New user</Button>
            <Button size="sm" variant="ghost" icon="plus" style={{ marginLeft: 6 }} onClick={() => setUserForm({ kind: 'role', name: '', password: '' })}>New role</Button>
          </div>
          {!extras && <Spinner />}
          {[...users, ...roles].map(u => (
            <div key={u.kind + u.name} className="relation">
              <Icon name={u.kind === 'role' ? 'layers' : 'user'} size={14} /><span className="strong">{u.name}</span>
              <span className="badge">{u.kind}</span>{u.kind === 'user' && !u.canLogin && <span className="badge warning">can’t sign in</span>}
              <div className="grow" />
              {u.kind === 'role' && users.length > 0 && <Button size="sm" variant="ghost" onClick={() => setMemberOf({ role: u.name, user: '' })}>Add a member</Button>}
              <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete" onClick={() => setChange({ change: { kind: 'dropUser', principal: u.name, oldName: u.kind }, title: `Delete the ${u.kind} ${u.name}?` })} />
            </div>
          ))}
        </div>
      )}

      {schema.capabilities.schemas && (
        <div className="card pad col gap-10">
          <div className="row"><div className="group-icon"><Icon name="folder" size={16} /></div><div className="grow" style={{ marginLeft: 10 }}><div className="strong">Schemas</div><div className="small subtle">Folders that group tables, e.g. sales and hr.</div></div>
            <Button size="sm" icon="plus" onClick={() => setSchemaName('')}>New schema</Button></div>
          <div className="row wrap gap-6">{schema.schemas.map(s => (
            <span key={s} className="chip" style={{ height: 26 }}>{s}<span className="tiny subtle">{schema.tables.filter(t => t.schema === s).length} tables</span>
              {!['dbo', 'public'].includes(s) && <button className="cell-link" title="Delete schema" onClick={() => setChange({ change: { kind: 'dropSchema', oldName: s }, title: `Delete the schema ${s}?` })}><Icon name="x" size={11} /></button>}
            </span>
          ))}</div>
        </div>
      )}

      {caps?.sequences && (
        <div className="card pad col gap-10">
          <div className="row"><div className="group-icon"><Icon name="list" size={16} /></div><div className="grow" style={{ marginLeft: 10 }}><div className="strong">Sequences</div><div className="small subtle">Number generators shared by tables (for ids, invoice numbers…).</div></div>
            <Button size="sm" icon="plus" onClick={() => setSeqForm({ name: '', start: '1' })}>New sequence</Button></div>
          {extras?.sequences.length === 0 && <div className="small subtle">No sequences.</div>}
          {extras?.sequences.map(s => (
            <div key={s.schema + s.name} className="relation">
              <span className="strong">{s.name}</span>{s.schema && schema.capabilities.schemas && <span className="tiny subtle">{s.schema}</span>}
              <span className="badge">current {s.current ?? 'not used yet'}</span>{s.increment && s.increment !== '1' && <span className="badge">step {s.increment}</span>}
              <div className="grow" />
              <Button size="sm" variant="ghost" onClick={() => setRestart({ name: s.name, schema: s.schema, value: '1' })}>Restart</Button>
              <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete" onClick={() => setChange({ change: { kind: 'dropSequence', schema: s.schema, table: s.name }, title: `Delete the sequence ${s.name}?` })} />
            </div>
          ))}
        </div>
      )}

      {caps?.databases && (
        <div className="card pad row gap-10">
          <div className="group-icon"><Icon name="database" size={16} /></div>
          <div className="grow"><div className="strong">This database</div><div className="small subtle">Create new databases or delete this one from the database list (Switch database).</div></div>
          <Button size="sm" variant="danger" icon="trash" onClick={async () => {
            if (await confirm({ title: `Delete the database ${schema.database}?`, message: 'Everything in it is deleted for good. You’ll see exactly what runs before it happens.', confirmText: 'Continue', danger: true }))
              setChange({ change: { kind: 'dropDatabase', oldName: schema.database }, title: `Delete the database ${schema.database}?` })
          }}>Delete this database</Button>
        </div>
      )}

      {userForm && (
        <Modal title={userForm.kind === 'user' ? 'New user' : 'New role'} icon="user" size="narrow" onClose={() => setUserForm(null)}
          footer={<><Button onClick={() => setUserForm(null)}>Cancel</Button><Button variant="primary" disabled={!userForm.name.trim() || (userForm.kind === 'user' && userForm.password.length < 8)}
            onClick={() => { const f = userForm; setUserForm(null); setChange({ change: f.kind === 'user' ? { kind: 'createUser', principal: f.name.trim(), password: f.password } : { kind: 'createRole', principal: f.name.trim() }, title: `Create the ${f.kind} ${f.name.trim()}` }) }}>Continue</Button></>}>
          <div className="col gap-12">
            <Field label="Name" hint={p === 'MySql' && userForm.kind === 'user' ? 'Add @host to limit where they can sign in from, e.g. app@localhost.' : undefined}><input className="input" autoFocus value={userForm.name} onChange={e => setUserForm({ ...userForm, name: e.target.value.replace(/\s/g, '') })} /></Field>
            {userForm.kind === 'user' && <Field label="Password" hint="At least 8 characters. It’s sent to the database only and never saved by QosriStudio."><input className="input" type="password" value={userForm.password} onChange={e => setUserForm({ ...userForm, password: e.target.value })} autoComplete="new-password" /></Field>}
            {userForm.kind === 'user' && p === 'SqlServer' && <div className="tiny subtle">Creates a SQL Server sign-in and a user for it in this database. Needs server administrator rights.</div>}
          </div>
        </Modal>
      )}
      {memberOf && (
        <Modal title={`Add a member to ${memberOf.role}`} icon="layers" size="narrow" onClose={() => setMemberOf(null)}
          footer={<><Button onClick={() => setMemberOf(null)}>Cancel</Button><Button variant="primary" disabled={!memberOf.user} onClick={() => { const m = memberOf; setMemberOf(null); setChange({ change: { kind: 'addMember', oldName: m.role, principal: m.user }, title: `Add ${m.user} to ${m.role}` }) }}>Continue</Button></>}>
          <select className="select" value={memberOf.user} onChange={e => setMemberOf({ ...memberOf, user: e.target.value })}><option value="">Choose a user…</option>{users.map(u => <option key={u.name}>{u.name}</option>)}</select>
        </Modal>
      )}
      {seqForm && (
        <Modal title="New sequence" icon="list" size="narrow" onClose={() => setSeqForm(null)}
          footer={<><Button onClick={() => setSeqForm(null)}>Cancel</Button><Button variant="primary" disabled={!seqForm.name.trim()} onClick={() => { const f = seqForm; setSeqForm(null); setChange({ change: { kind: 'createSequence', schema: defaultSchema(schema), newName: f.name.trim(), sql: f.start }, title: `Create the sequence ${f.name}` }) }}>Continue</Button></>}>
          <div className="col gap-12">
            <Field label="Name"><input className="input" autoFocus value={seqForm.name} onChange={e => setSeqForm({ ...seqForm, name: e.target.value.replace(/\s/g, '_') })} /></Field>
            <Field label="First number"><input className="input" inputMode="numeric" value={seqForm.start} onChange={e => setSeqForm({ ...seqForm, start: e.target.value.replace(/[^\d-]/g, '') })} /></Field>
          </div>
        </Modal>
      )}
      {restart && (
        <Modal title={`Restart ${restart.name}`} icon="refresh" size="narrow" onClose={() => setRestart(null)}
          footer={<><Button onClick={() => setRestart(null)}>Cancel</Button><Button variant="primary" onClick={() => { const r = restart; setRestart(null); setChange({ change: { kind: 'restartSequence', schema: r.schema, table: r.name, sql: r.value }, title: `Restart ${r.name} at ${r.value}` }) }}>Continue</Button></>}>
          <Field label="Next number"><input className="input" autoFocus inputMode="numeric" value={restart.value} onChange={e => setRestart({ ...restart, value: e.target.value.replace(/[^\d-]/g, '') })} /></Field>
        </Modal>
      )}
      {schemaName !== null && (
        <Modal title="New schema" icon="folder" size="narrow" onClose={() => setSchemaName(null)}
          footer={<><Button onClick={() => setSchemaName(null)}>Cancel</Button><Button variant="primary" disabled={!schemaName.trim()} onClick={() => { const n = schemaName.trim(); setSchemaName(null); setChange({ change: { kind: 'createSchema', newName: n }, title: `Create the schema ${n}` }) }}>Continue</Button></>}>
          <Field label="Name"><input className="input" autoFocus value={schemaName} onChange={e => setSchemaName(e.target.value.replace(/\s/g, '_'))} /></Field>
        </Modal>
      )}
      {change && <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)} onDone={() => {
        const c = change.change; setChange(null)
        if (c.kind === 'dropDatabase') { window.location.hash = window.location.hash.replace(/&d=[^&]*/, ''); return }
        onChanged()
      }} />}
    </div>
  )
}
