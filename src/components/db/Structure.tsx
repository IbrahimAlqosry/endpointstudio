import { useEffect, useState } from 'react'
import { api } from '../../api'
import type { DbRef, DbSchema, DbsColumnDef, DbsTable, DdlChange } from '../../types'
import { Icon } from '../Icon'
import { Alert, Button, Field, Modal, Spinner, Switch, errorText, useToast } from '../ui'
import { tableKey } from './ErDiagram'

/** Shows what a change will do, then makes it. */
export function ChangeDialog({ dbRef, change, title, onClose, onDone }: { dbRef: DbRef; change: DdlChange; title: string; onClose: () => void; onDone: () => void }) {
  const toast = useToast()
  const [preview, setPreview] = useState<{ statements: string[]; warning: string | null } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [showSql, setShowSql] = useState(false)
  useEffect(() => { api.dbsPreview(dbRef, change).then(setPreview).catch(e => setError(errorText(e))) }, [])
  const danger = /^drop|truncate/.test(change.kind)
  const apply = async () => {
    setBusy(true); setError(null)
    try { await api.dbsApply(dbRef, change); toast.success('Done. The database was updated.'); onDone() }
    catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }
  return (
    <Modal title={title} icon={danger ? 'alert' : 'database'} size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant={danger ? 'danger' : 'primary'} loading={busy} disabled={!preview} onClick={apply}>{danger ? 'Yes, do it' : 'Apply change'}</Button></>}>
      <div className="col gap-12">
        {!preview && !error && <div className="row small subtle"><Spinner /> Checking the change…</div>}
        {error && <Alert kind="error">{error}</Alert>}
        {preview?.warning && <Alert kind={danger ? 'warning' : 'info'}>{preview.warning}</Alert>}
        {preview && !preview.warning && <div className="muted small">Ready to apply. Nothing changes until you click the button.</div>}
        {preview && (
          <div>
            <button className="btn ghost sm" onClick={() => setShowSql(s => !s)}><Icon name={showSql ? 'chevronDown' : 'chevronRight'} size={13} />What will run</button>
            {showSql && <pre className="log" style={{ marginTop: 8, whiteSpace: 'pre-wrap' }}>{preview.statements.join(';\n\n')};</pre>}
          </div>
        )}
      </div>
    </Modal>
  )
}

function TypePicker({ value, onChange, types }: { value: string; onChange: (v: string) => void; types: string[] }) {
  return (
    <>
      <input className="input" list="db-types" value={value} onChange={e => onChange(e.target.value)} placeholder="type" />
      <datalist id="db-types">{types.map(t => <option key={t} value={t} />)}</datalist>
    </>
  )
}

const blankColumn = (): DbsColumnDef => ({ name: '', type: '', nullable: true, default: null, identity: false, primaryKey: false })

export function CreateTableDialog({ dbRef, schema, onClose, onDone }: { dbRef: DbRef; schema: DbSchema; onClose: () => void; onDone: (key: string) => void }) {
  const p = schema.provider
  const intType = p === 'Oracle' ? 'NUMBER(10)' : p === 'PostgreSql' ? 'integer' : p === 'Sqlite' ? 'INTEGER' : 'int'
  const textType = schema.types.find(t => /varchar2?\(255\)|nvarchar\(255\)|^TEXT$/i.test(t)) ?? schema.types[0]
  const defaultSchema = schema.capabilities.schemas ? (schema.schemas.includes('dbo') ? 'dbo' : schema.schemas.includes('public') ? 'public' : schema.schemas[0] ?? '') : schema.tables[0]?.schema ?? ''
  const [name, setName] = useState('')
  const [sch, setSch] = useState(defaultSchema)
  const [cols, setCols] = useState<DbsColumnDef[]>([
    { name: p === 'Oracle' ? 'ID' : 'Id', type: intType, nullable: false, default: null, identity: true, primaryKey: true },
    { name: p === 'Oracle' ? 'NAME' : 'Name', type: textType, nullable: false, default: null, identity: false, primaryKey: false },
  ])
  const [relations, setRelations] = useState<{ column: string; refTable: string }[]>([])
  const [step, setStep] = useState<'edit' | 'confirm'>('edit')
  const set = (i: number, patch: Partial<DbsColumnDef>) => setCols(c => c.map((x, j) => j === i ? { ...x, ...patch } : x))
  const valid = name.trim() && cols.length > 0 && cols.every(c => c.name.trim() && c.type.trim())
  const change: DdlChange = { kind: 'createTable', schema: sch, table: name.trim(), columns: cols.map(c => ({ ...c, name: c.name.trim(), type: c.type.trim(), default: c.default?.trim() || null })) }
  const [created, setCreated] = useState(false)
  const toast = useToast()

  // Relations are added once the table exists
  const finish = async () => {
    const key = tableKey({ schema: sch, name: name.trim() })
    for (const r of relations.filter(r => r.column && r.refTable)) {
      const parent = schema.tables.find(t => tableKey(t) === r.refTable)
      if (!parent || parent.primaryKey.length !== 1) continue
      try {
        await api.dbsApply(dbRef, { kind: 'addForeignKey', schema: sch, table: name.trim(), keyColumns: [r.column], refSchema: parent.schema, refTable: parent.name, refColumns: parent.primaryKey, constraintName: `FK_${name.trim()}_${parent.name}`.slice(0, 60) })
      } catch (e) { toast.error(`The relation to ${parent.name} couldn’t be added: ${errorText(e)}`) }
    }
    onDone(key)
  }

  if (step === 'confirm' && !created) return <ChangeDialog dbRef={dbRef} change={change} title={`Create ${name}`} onClose={() => setStep('edit')} onDone={() => { setCreated(true); finish() }} />

  return (
    <Modal title="New table" subtitle="Describe the columns. You can change them later." icon="table" size="wide" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!valid} onClick={() => setStep('confirm')}>Continue</Button></>}>
      <div className="col gap-16">
        <div className="grid-2">
          <Field label="Table name"><input className="input" autoFocus value={name} onChange={e => setName(e.target.value.replace(/\s/g, '_'))} placeholder={p === 'Oracle' ? 'CUSTOMERS' : 'Customers'} /></Field>
          {schema.capabilities.schemas && (
            <Field label="Schema">
              <select className="select" value={sch} onChange={e => setSch(e.target.value)}>{schema.schemas.map(s => <option key={s}>{s}</option>)}</select>
            </Field>
          )}
        </div>
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>Column</th><th>Type</th><th>Required</th><th>Key</th><th>Auto number</th><th>Default</th><th /></tr></thead>
            <tbody>
              {cols.map((c, i) => (
                <tr key={i}>
                  <td><input className="input" value={c.name} onChange={e => set(i, { name: e.target.value.replace(/\s/g, '_') })} placeholder="name" /></td>
                  <td style={{ minWidth: 150 }}><TypePicker value={c.type} onChange={v => set(i, { type: v })} types={schema.types} /></td>
                  <td><input type="checkbox" checked={!c.nullable} onChange={e => set(i, { nullable: !e.target.checked })} /></td>
                  <td><input type="checkbox" checked={c.primaryKey} onChange={e => set(i, { primaryKey: e.target.checked, nullable: e.target.checked ? false : c.nullable })} /></td>
                  <td><input type="checkbox" checked={c.identity} onChange={e => set(i, { identity: e.target.checked })} /></td>
                  <td><input className="input" value={c.default ?? ''} onChange={e => set(i, { default: e.target.value })} placeholder="none" /></td>
                  <td><Button size="sm" variant="ghost" iconOnly icon="trash" onClick={() => setCols(cs => cs.filter((_, j) => j !== i))} aria-label="Remove" /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div><Button size="sm" icon="plus" onClick={() => setCols(c => [...c, blankColumn()])}>Add column</Button></div>
        {schema.capabilities.addForeignKey && schema.tables.length > 0 && (
          <Field label="Relations" hint="Point a column at another table's key, e.g. Orders.CustomerId → Customers.">
            <div className="col gap-6">
              {relations.map((r, i) => (
                <div key={i} className="row gap-6">
                  <select className="select" value={r.column} onChange={e => setRelations(rs => rs.map((x, j) => j === i ? { ...x, column: e.target.value } : x))}>
                    <option value="">Column…</option>{cols.filter(c => c.name).map(c => <option key={c.name}>{c.name}</option>)}
                  </select>
                  <Icon name="arrowRight" size={14} />
                  <select className="select" value={r.refTable} onChange={e => setRelations(rs => rs.map((x, j) => j === i ? { ...x, refTable: e.target.value } : x))}>
                    <option value="">Table…</option>{schema.tables.filter(t => t.primaryKey.length === 1).map(t => <option key={tableKey(t)} value={tableKey(t)}>{tableKey(t)}</option>)}
                  </select>
                  <Button size="sm" variant="ghost" iconOnly icon="x" onClick={() => setRelations(rs => rs.filter((_, j) => j !== i))} aria-label="Remove" />
                </div>
              ))}
              <div><Button size="sm" variant="ghost" icon="plus" onClick={() => setRelations(r => [...r, { column: '', refTable: '' }])}>Add relation</Button></div>
            </div>
          </Field>
        )}
      </div>
    </Modal>
  )
}

function ColumnDialog({ table, schema, column, onClose, onSubmit }: {
  table: DbsTable; schema: DbSchema; column?: DbsColumnDef; onClose: () => void; onSubmit: (c: DbsColumnDef, newName: string | null) => void
}) {
  const [c, setC] = useState<DbsColumnDef>(column ?? blankColumn())
  const [name, setName] = useState(column?.name ?? '')
  const editing = !!column
  return (
    <Modal title={editing ? `Change ${column!.name}` : `Add a column to ${table.name}`} icon="edit" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!name.trim() || !c.type.trim()} onClick={() => onSubmit({ ...c, name: editing ? column!.name : name.trim(), default: c.default?.trim() || null }, editing && name.trim() !== column!.name ? name.trim() : null)}>Continue</Button></>}>
      <div className="col gap-14">
        <Field label="Name"><input className="input" autoFocus value={name} onChange={e => setName(e.target.value.replace(/\s/g, '_'))} /></Field>
        {(!editing || schema.capabilities.alterColumn) && <Field label="Type"><TypePicker value={c.type} onChange={v => setC({ ...c, type: v })} types={schema.types} /></Field>}
        {(!editing || schema.capabilities.alterColumn) && (
          <>
            <Switch checked={!c.nullable} onChange={v => setC({ ...c, nullable: !v })} label="Required (can’t be empty)" />
            <Field label="Default value" hint="Used when a row doesn't give one. Text goes in quotes, e.g. 'new'. Leave empty for none.">
              <input className="input" value={c.default ?? ''} onChange={e => setC({ ...c, default: e.target.value })} />
            </Field>
          </>
        )}
        {!editing && <Switch checked={c.identity} onChange={v => setC({ ...c, identity: v })} label="Number automatically" />}
        {editing && !schema.capabilities.alterColumn && <Alert kind="info">This database can only rename existing columns.</Alert>}
      </div>
    </Modal>
  )
}

function RelationDialog({ table, schema, onClose, onSubmit }: { table: DbsTable; schema: DbSchema; onClose: () => void; onSubmit: (c: DdlChange) => void }) {
  const [column, setColumn] = useState('')
  const [ref, setRef] = useState('')
  const [onDelete, setOnDelete] = useState('NO ACTION')
  const parent = schema.tables.find(t => tableKey(t) === ref)
  return (
    <Modal title={`New relation from ${table.name}`} subtitle="A column that holds the key of a row in another table." icon="arrowRight" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!column || !parent || parent.primaryKey.length !== 1}
        onClick={() => onSubmit({ kind: 'addForeignKey', schema: table.schema, table: table.name, keyColumns: [column], refSchema: parent!.schema, refTable: parent!.name, refColumns: parent!.primaryKey, onDelete, constraintName: `FK_${table.name}_${parent!.name}_${column}`.slice(0, 60) })}>Continue</Button></>}>
      <div className="col gap-14">
        <Field label="Column in this table"><select className="select" value={column} onChange={e => setColumn(e.target.value)}><option value="">Choose…</option>{table.columns.map(c => <option key={c.name}>{c.name}</option>)}</select></Field>
        <Field label="Points to" hint={parent ? `Its key: ${parent.primaryKey.join(', ') || 'none'}` : undefined}>
          <select className="select" value={ref} onChange={e => setRef(e.target.value)}><option value="">Choose a table…</option>{schema.tables.map(t => <option key={tableKey(t)} value={tableKey(t)}>{tableKey(t)}</option>)}</select>
        </Field>
        {parent && parent.primaryKey.length !== 1 && <Alert kind="warning">That table needs a single-column key to be pointed at.</Alert>}
        <Field label="When the other row is deleted">
          <select className="select" value={onDelete} onChange={e => setOnDelete(e.target.value)}>
            <option value="NO ACTION">Block the delete</option>
            <option value="CASCADE">Delete these rows too</option>
            <option value="SET NULL">Make this column empty</option>
          </select>
        </Field>
      </div>
    </Modal>
  )
}

function IndexDialog({ table, onClose, onSubmit }: { table: DbsTable; onClose: () => void; onSubmit: (c: DdlChange) => void }) {
  const [cols, setCols] = useState<string[]>([])
  const [unique, setUnique] = useState(false)
  return (
    <Modal title={`New index on ${table.name}`} subtitle="Makes searching and sorting by these columns faster." icon="zap" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={cols.length === 0} onClick={() => onSubmit({ kind: 'addIndex', schema: table.schema, table: table.name, keyColumns: cols, unique, constraintName: `IX_${table.name}_${cols.join('_')}`.slice(0, 60) })}>Continue</Button></>}>
      <div className="col gap-14">
        <Field label="Columns (in order)">
          <div className="col gap-4">
            {table.columns.map(c => (
              <label key={c.name} className="row gap-6 small"><input type="checkbox" checked={cols.includes(c.name)} onChange={e => setCols(x => e.target.checked ? [...x, c.name] : x.filter(y => y !== c.name))} />{c.name}<span className="tiny subtle">{c.type}</span>{cols.includes(c.name) && <span className="badge">{cols.indexOf(c.name) + 1}</span>}</label>
            ))}
          </div>
        </Field>
        <Switch checked={unique} onChange={setUnique} label="Values must be unique" />
      </div>
    </Modal>
  )
}

function NameDialog({ title, label, initial, onClose, onSubmit }: { title: string; label: string; initial: string; onClose: () => void; onSubmit: (v: string) => void }) {
  const [v, setV] = useState(initial)
  return (
    <Modal title={title} icon="edit" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!v.trim() || v.trim() === initial} onClick={() => onSubmit(v.trim())}>Continue</Button></>}>
      <Field label={label}><input className="input" autoFocus value={v} onChange={e => setV(e.target.value.replace(/\s/g, '_'))} /></Field>
    </Modal>
  )
}

export function StructurePanel({ dbRef, schema, table, onChanged, onOpenTable, onDropped }: {
  dbRef: DbRef; schema: DbSchema; table: DbsTable; onChanged: () => void; onOpenTable: (key: string) => void; onDropped: () => void
}) {
  const [dialog, setDialog] = useState<null | { kind: 'column'; column?: DbsColumnDef } | { kind: 'relation' } | { kind: 'index' } | { kind: 'rename' }>(null)
  const [change, setChange] = useState<{ change: DdlChange; title: string; after?: () => void } | null>(null)
  const outgoing = schema.foreignKeys.filter(f => f.schema === table.schema && f.table === table.name)
  const incoming = schema.foreignKeys.filter(f => f.refSchema === table.schema && f.refTable === table.name && !(f.table === table.name && f.schema === table.schema))
  const indexes = schema.indexes.filter(i => i.schema === table.schema && i.table === table.name)
  const base = { schema: table.schema, table: table.name }
  const caps = schema.capabilities
  const run = (c: DdlChange, title: string, after?: () => void) => { setDialog(null); setChange({ change: c, title, after }) }

  return (
    <div className="col gap-20">
      <section className="col gap-8">
        <div className="row"><h3 className="section-title grow">Columns <span className="badge">{table.columns.length}</span></h3><Button size="sm" icon="plus" onClick={() => setDialog({ kind: 'column' })}>Add column</Button></div>
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>Name</th><th>Type</th><th>Required</th><th>Default</th><th /></tr></thead>
            <tbody>
              {table.columns.map(c => {
                const fk = outgoing.find(f => f.columns.includes(c.name))
                return (
                  <tr key={c.name}>
                    <td>
                      <span className="row gap-6">
                        {c.primaryKey && <span className="badge warning" title="Key"><Icon name="key" size={11} />Key</span>}
                        <span className="strong">{c.name}</span>
                        {c.identity && <span className="badge accent">auto number</span>}
                        {c.computed && <span className="badge">calculated</span>}
                        {fk && <button className="chip route" onClick={() => onOpenTable(tableKey({ schema: fk.refSchema, name: fk.refTable }))}>→ {fk.refTable}</button>}
                      </span>
                    </td>
                    <td className="mono">{c.type}</td>
                    <td>{c.nullable ? <span className="subtle">optional</span> : 'required'}</td>
                    <td className="mono small">{c.default ?? <span className="subtle">—</span>}</td>
                    <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                      <Button size="sm" variant="ghost" iconOnly icon="edit" title="Change" onClick={() => setDialog({ kind: 'column', column: { name: c.name, type: c.type, nullable: c.nullable, default: c.default, identity: c.identity, primaryKey: c.primaryKey } })} />
                      <Button size="sm" variant="ghost" iconOnly icon="trash" title="Delete column" disabled={table.columns.length < 2} onClick={() => run({ kind: 'dropColumn', ...base, oldName: c.name }, `Delete the column ${c.name}?`)} />
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        <div className="row wrap gap-6 small subtle">
          <span>Key: {table.primaryKey.length ? table.primaryKey.join(', ') : 'none'}</span>
          {caps.alterColumn && <KeyEditor table={table} onSubmit={keys => run({ kind: 'setPrimaryKey', ...base, keyColumns: keys, constraintName: table.pkName && table.pkName !== 'PRIMARY' ? table.pkName : `PK_${table.name}` }, 'Change the key')} />}
        </div>
      </section>

      <section className="col gap-8">
        <div className="row"><h3 className="section-title grow">Relations <span className="badge">{outgoing.length + incoming.length}</span></h3>{caps.addForeignKey && <Button size="sm" icon="plus" onClick={() => setDialog({ kind: 'relation' })}>Add relation</Button>}</div>
        {outgoing.length + incoming.length === 0 && <div className="small subtle">This table isn’t related to any other table.</div>}
        <div className="col gap-6">
          {outgoing.map(f => (
            <div key={f.name} className="relation">
              <span className="strong">{table.name}</span><span className="mono small">{f.columns.join(', ')}</span>
              <Icon name="arrowRight" size={14} />
              <button className="link" onClick={() => onOpenTable(tableKey({ schema: f.refSchema, name: f.refTable }))}>{f.refTable}</button><span className="mono small">{f.refColumns.join(', ')}</span>
              <span className="badge">{f.onDelete === 'CASCADE' ? 'deleting the other row deletes these' : f.onDelete === 'SET NULL' ? 'deleting the other row empties this' : 'other row can’t be deleted while used'}</span>
              <div className="grow" />
              <span className="tiny subtle">{f.name}</span>
              {caps.addForeignKey && <Button size="sm" variant="ghost" iconOnly icon="trash" title="Remove relation" onClick={() => run({ kind: 'dropForeignKey', ...base, constraintName: f.name }, `Remove the relation ${f.name}?`)} />}
            </div>
          ))}
          {incoming.map(f => (
            <div key={f.schema + f.table + f.name} className="relation incoming">
              <button className="link" onClick={() => onOpenTable(tableKey({ schema: f.schema, name: f.table }))}>{f.table}</button><span className="mono small">{f.columns.join(', ')}</span>
              <Icon name="arrowRight" size={14} />
              <span className="strong">{table.name}</span><span className="mono small">{f.refColumns.join(', ')}</span>
              <span className="badge accent">points here</span>
              <div className="grow" /><span className="tiny subtle">{f.name}</span>
            </div>
          ))}
        </div>
      </section>

      <section className="col gap-8">
        <div className="row"><h3 className="section-title grow">Indexes <span className="badge">{indexes.length}</span></h3><Button size="sm" icon="plus" onClick={() => setDialog({ kind: 'index' })}>Add index</Button></div>
        {indexes.length === 0 && <div className="small subtle">No extra indexes. The key is always indexed.</div>}
        {indexes.map(i => (
          <div key={i.name} className="relation">
            <span className="strong">{i.name}</span>
            <span className="mono small">{i.columns.join(', ')}</span>
            {i.unique && <span className="badge warning">unique</span>}
            <div className="grow" />
            <Button size="sm" variant="ghost" iconOnly icon="trash" title="Remove index" onClick={() => run({ kind: 'dropIndex', ...base, constraintName: i.name }, `Remove the index ${i.name}?`)} />
          </div>
        ))}
      </section>

      <section className="col gap-8">
        <h3 className="section-title">Table</h3>
        <div className="row wrap gap-6">
          <Button size="sm" icon="edit" onClick={() => setDialog({ kind: 'rename' })}>Rename table</Button>
          <Button size="sm" icon="ban" onClick={() => run({ kind: 'truncateTable', ...base }, `Empty ${table.name}?`, onChanged)}>Remove all rows</Button>
          <Button size="sm" variant="danger" icon="trash" onClick={() => run({ kind: 'dropTable', ...base }, `Delete the table ${table.name}?`, onDropped)}>Delete table</Button>
        </div>
      </section>

      {dialog?.kind === 'column' && (
        <ColumnDialog table={table} schema={schema} column={dialog.column} onClose={() => setDialog(null)} onSubmit={async (c, newName) => {
          const old = dialog.column
          if (!old) return run({ kind: 'addColumn', ...base, column: c }, `Add the column ${c.name}`)
          const changed = old.type !== c.type || old.nullable !== c.nullable || (old.default ?? null) !== (c.default ?? null)
          if (changed && caps.alterColumn) {
            setDialog(null)
            setChange({ change: { kind: 'alterColumn', ...base, column: c }, title: `Change ${old.name}`, after: newName ? () => setChange({ change: { kind: 'renameColumn', ...base, oldName: old.name, newName }, title: `Rename ${old.name} to ${newName}` }) : undefined })
          } else if (newName) run({ kind: 'renameColumn', ...base, oldName: old.name, newName }, `Rename ${old.name} to ${newName}`)
          else setDialog(null)
        }} />
      )}
      {dialog?.kind === 'relation' && <RelationDialog table={table} schema={schema} onClose={() => setDialog(null)} onSubmit={c => run(c, 'Add the relation')} />}
      {dialog?.kind === 'index' && <IndexDialog table={table} onClose={() => setDialog(null)} onSubmit={c => run(c, 'Add the index')} />}
      {dialog?.kind === 'rename' && <NameDialog title={`Rename ${table.name}`} label="New name" initial={table.name} onClose={() => setDialog(null)} onSubmit={v => run({ kind: 'renameTable', ...base, newName: v }, `Rename ${table.name} to ${v}`, () => onOpenTable(tableKey({ schema: table.schema, name: v })))} />}
      {change && (
        <ChangeDialog dbRef={dbRef} change={change.change} title={change.title} onClose={() => setChange(null)}
          onDone={() => { const after = change.after; setChange(null); onChanged(); after?.() }} />
      )}
    </div>
  )
}

function KeyEditor({ table, onSubmit }: { table: DbsTable; onSubmit: (keys: string[]) => void }) {
  const [open, setOpen] = useState(false)
  const [keys, setKeys] = useState<string[]>(table.primaryKey)
  if (!open) return <button className="btn ghost sm" onClick={() => setOpen(true)}>Change key</button>
  return (
    <Modal title={`Key of ${table.name}`} subtitle="The columns that identify each row. Their values must be unique and not empty." icon="key" size="narrow" onClose={() => setOpen(false)}
      footer={<><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="primary" onClick={() => { setOpen(false); onSubmit(keys) }}>Continue</Button></>}>
      <div className="col gap-4">
        {table.columns.map(c => (
          <label key={c.name} className="row gap-6 small"><input type="checkbox" checked={keys.includes(c.name)} onChange={e => setKeys(k => e.target.checked ? [...k, c.name] : k.filter(x => x !== c.name))} />{c.name}<span className="tiny subtle">{c.type}</span></label>
        ))}
      </div>
    </Modal>
  )
}
