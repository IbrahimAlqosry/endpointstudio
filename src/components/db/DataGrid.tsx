import { useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../../api'
import type { DbRef, DbSchema, DbsColumn, DbsTable, RowsResult } from '../../types'
import { Icon } from '../Icon'
import { Alert, Button, Field, Modal, Spinner, errorText, useConfirm, useToast } from '../ui'
import { tableKey } from './ErDiagram'

const PAGE_SIZES = [25, 50, 100, 250]

export function DataGrid({ dbRef, schema, table, initialFilters, readOnly, onOpenTable, onChanged }: {
  dbRef: DbRef
  schema: DbSchema
  table: DbsTable
  initialFilters?: Record<string, string>
  readOnly?: boolean
  onOpenTable: (key: string, filters?: Record<string, string>) => void
  onChanged: () => void
}) {
  const toast = useToast()
  const confirm = useConfirm()
  const [data, setData] = useState<RowsResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [offset, setOffset] = useState(0)
  const [limit, setLimit] = useState(50)
  const [order, setOrder] = useState<{ col: string; desc: boolean } | null>(null)
  const [search, setSearch] = useState('')
  const [appliedSearch, setAppliedSearch] = useState('')
  const [filters, setFilters] = useState<Record<string, string>>(initialFilters ?? {})
  const [showFilters, setShowFilters] = useState(!!initialFilters && Object.keys(initialFilters).length > 0)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [editing, setEditing] = useState<{ row: number; col: number; value: string; isNull: boolean } | null>(null)
  const [form, setForm] = useState<{ mode: 'add' | 'edit'; row?: (string | null)[] } | null>(null)
  const [importOpen, setImportOpen] = useState(false)
  const [cellView, setCellView] = useState<{ col: string; value: string | null } | null>(null)
  const reqId = useRef(0)

  const activeFilters = Object.fromEntries(Object.entries(filters).filter(([, v]) => v.trim() !== ''))
  const filterKey = JSON.stringify(activeFilters)

  const load = async () => {
    const id = ++reqId.current
    setLoading(true); setError(null)
    try {
      const r = await api.dbsRows(dbRef, table.schema, table.name, { offset, limit, orderBy: order?.col, desc: order?.desc, search: appliedSearch, filters: activeFilters })
      if (id !== reqId.current) return
      setData(r); setSelected(new Set())
    } catch (e) { if (id === reqId.current) setError(errorText(e)) } finally { if (id === reqId.current) setLoading(false) }
  }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { load() }, [offset, limit, order?.col, order?.desc, appliedSearch, filterKey, tableKey(table)])
  useEffect(() => { const t = setTimeout(() => { setOffset(0); setAppliedSearch(search) }, 350); return () => clearTimeout(t) }, [search])

  const columns = useMemo(() => (data?.columns ?? []).map(n => table.columns.find(c => c.name === n) ?? ({ name: n, type: '', nullable: true, default: null, identity: false, primaryKey: false, computed: true, position: 0, maxLength: null } as DbsColumn)), [data, table])
  const fkOf = (col: string) => schema.foreignKeys.find(f => f.schema === table.schema && f.table === table.name && f.columns.length === 1 && f.columns[0] === col)
  const canEdit = !readOnly && !data?.readOnly
  const keysOf = (row: (string | null)[]) => Object.fromEntries((data?.keyColumns ?? []).map(k => [k, row[data!.columns.indexOf(k)]]))

  const saveCell = async () => {
    if (!editing || !data) return
    const row = data.rows[editing.row]
    const col = data.columns[editing.col]
    const value = editing.isNull ? null : editing.value
    setEditing(null)
    if (value === row[editing.col]) return
    try {
      await api.dbsUpdate(dbRef, table.schema, table.name, keysOf(row), { [col]: value })
      toast.success('Saved.')
      load()
    } catch (e) { toast.error(errorText(e)) }
  }

  const removeSelected = async () => {
    if (!data || selected.size === 0) return
    const children = schema.foreignKeys.filter(f => f.refSchema === table.schema && f.refTable === table.name)
    const cascade = children.filter(f => f.onDelete === 'CASCADE').map(f => f.table)
    const ok = await confirm({
      title: `Delete ${selected.size} row${selected.size === 1 ? '' : 's'}?`,
      message: <>This can’t be undone.{cascade.length > 0 && <> Matching rows in <b>{[...new Set(cascade)].join(', ')}</b> will be deleted too.</>}</>,
      confirmText: 'Delete', danger: true,
    })
    if (!ok) return
    try {
      const r = await api.dbsDelete(dbRef, table.schema, table.name, [...selected].map(i => keysOf(data.rows[i])))
      toast.success(`Deleted ${r.affected} row${r.affected === 1 ? '' : 's'}.`)
      load(); onChanged()
    } catch (e) { toast.error(errorText(e)) }
  }

  const exportAs = async (format: string) => {
    try {
      const blob = await api.dbsExport(dbRef, table.schema, table.name, format, appliedSearch)
      const a = document.createElement('a')
      a.href = URL.createObjectURL(blob)
      a.download = `${table.name}.${format}`
      a.click()
      setTimeout(() => URL.revokeObjectURL(a.href), 2000)
    } catch (e) { toast.error(errorText(e)) }
  }

  const total = data?.total ?? 0
  const sortBy = (col: string) => { setOffset(0); setOrder(o => o?.col === col ? (o.desc ? null : { col, desc: true }) : { col, desc: false }) }

  return (
    <div className="col gap-10">
      <div className="row wrap gap-6">
        <div className="search" style={{ maxWidth: 340 }}><Icon name="search" size={16} /><input className="input" placeholder={`Search ${table.name}…`} value={search} onChange={e => setSearch(e.target.value)} /></div>
        <Button size="sm" variant={showFilters ? 'primary' : 'default'} icon="filter" onClick={() => setShowFilters(s => !s)}>Filter{Object.keys(activeFilters).length ? ` (${Object.keys(activeFilters).length})` : ''}</Button>
        <Button size="sm" variant="ghost" icon="refresh" onClick={load} loading={loading}>Refresh</Button>
        <div className="grow" />
        {canEdit && selected.size > 0 && <Button size="sm" variant="danger" icon="trash" onClick={removeSelected}>Delete {selected.size}</Button>}
        {canEdit && selected.size === 1 && <Button size="sm" icon="edit" onClick={() => setForm({ mode: 'edit', row: data!.rows[[...selected][0]] })}>Edit row</Button>}
        {canEdit && <Button size="sm" icon="download" onClick={() => setImportOpen(true)} style={{ transform: 'none' }}>Import</Button>}
        <ExportMenu onPick={exportAs} />
        {canEdit && <Button size="sm" variant="primary" icon="plus" onClick={() => setForm({ mode: 'add' })}>Add row</Button>}
      </div>
      {!canEdit && <div className="tiny subtle">This is a view, so its rows can be read but not changed here.</div>}
      {canEdit && data && table.primaryKey.length === 0 && <Alert kind="warning">This table has no key. Rows are matched by all their values, so editing identical rows isn’t possible.</Alert>}
      {error && <Alert kind="error">{error}</Alert>}

      <div className="table-wrap grid-wrap">
        <table className="table data-grid">
          <thead>
            <tr>
              {canEdit && (
                <th style={{ width: 34 }}>
                  <input type="checkbox" checked={!!data && data.rows.length > 0 && selected.size === data.rows.length}
                    onChange={e => setSelected(e.target.checked && data ? new Set(data.rows.map((_, i) => i)) : new Set())} />
                </th>
              )}
              {columns.map(c => (
                <th key={c.name} onClick={() => sortBy(c.name)} className="sortable" title={`${c.type}${c.nullable ? '' : ' · required'}${c.default ? ` · default ${c.default}` : ''}`}>
                  <span className="row gap-4" style={{ flexWrap: 'nowrap' }}>
                    {c.primaryKey && <Icon name="key" size={12} />}
                    {fkOf(c.name) && <Icon name="arrowRight" size={12} />}
                    <span>{c.name}</span>
                    {order?.col === c.name && <Icon name={order.desc ? 'chevronDown' : 'chevronRight'} size={12} />}
                  </span>
                  <div className="th-type">{c.type}</div>
                </th>
              ))}
            </tr>
            {showFilters && (
              <tr className="filter-row">
                {canEdit && <th />}
                {columns.map(c => (
                  <th key={c.name}>
                    <input className="input" placeholder="contains…" value={filters[c.name] ?? ''} title="Type text to match, or >10, <=5, =abc, (empty), (not empty)"
                      onChange={e => { setOffset(0); setFilters(f => ({ ...f, [c.name]: e.target.value })) }} />
                  </th>
                ))}
              </tr>
            )}
          </thead>
          <tbody>
            {data?.rows.map((row, ri) => (
              <tr key={ri} className={selected.has(ri) ? 'selected' : ''}>
                {canEdit && (
                  <td><input type="checkbox" checked={selected.has(ri)} onChange={e => setSelected(s => { const n = new Set(s); if (e.target.checked) n.add(ri); else n.delete(ri); return n })} /></td>
                )}
                {row.map((v, ci) => {
                  const col = columns[ci]
                  const isEditing = editing?.row === ri && editing.col === ci
                  const fk = fkOf(col.name)
                  return (
                    <td key={ci} className={`${v === null ? 'null' : ''}${isEditing ? ' editing' : ''}`}
                      onDoubleClick={() => { if (canEdit && !col.computed && !col.identity) setEditing({ row: ri, col: ci, value: v ?? '', isNull: v === null }); else setCellView({ col: col.name, value: v }) }}>
                      {isEditing ? (
                        <div className="row gap-4" style={{ flexWrap: 'nowrap' }}>
                          <input className="input" autoFocus value={editing.isNull ? '' : editing.value} placeholder={editing.isNull ? 'empty' : ''}
                            onChange={e => setEditing({ ...editing, value: e.target.value, isNull: false })}
                            onKeyDown={e => { if (e.key === 'Enter') saveCell(); if (e.key === 'Escape') setEditing(null) }} />
                          {col.nullable && <button className="btn ghost sm" title="Make empty" onMouseDown={e => e.preventDefault()} onClick={() => setEditing({ ...editing, isNull: true, value: '' })}>∅</button>}
                          <button className="btn primary sm" onMouseDown={e => e.preventDefault()} onClick={saveCell}><Icon name="check" size={13} /></button>
                        </div>
                      ) : (
                        <span className="row gap-4" style={{ flexWrap: 'nowrap' }}>
                          <span className="cell" title={v ?? 'empty'}>{v === null ? 'empty' : v.length > 120 ? v.slice(0, 120) + '…' : v}</span>
                          {fk && v !== null && (
                            <button className="cell-link" title={`Open the ${fk.refTable} row`} onClick={() => onOpenTable(tableKey({ schema: fk.refSchema, name: fk.refTable }), { [fk.refColumns[0]]: `=${v}` })}>
                              <Icon name="external" size={12} />
                            </button>
                          )}
                        </span>
                      )}
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
        {loading && !data && <div className="empty"><Spinner large /></div>}
        {data && data.rows.length === 0 && <div className="empty small subtle">{appliedSearch || Object.keys(activeFilters).length ? 'No rows match.' : 'This table is empty.'}</div>}
      </div>

      <div className="row wrap small subtle">
        <span>{total === 0 ? 'No rows' : `${(offset + 1).toLocaleString()}–${Math.min(offset + limit, total).toLocaleString()} of ${total.toLocaleString()} rows`}</span>
        {canEdit && <span className="tiny">· double-click a value to change it</span>}
        <div className="grow" />
        <select className="select" style={{ width: 'auto', height: 30 }} value={limit} onChange={e => { setOffset(0); setLimit(Number(e.target.value)) }}>
          {PAGE_SIZES.map(n => <option key={n} value={n}>{n} per page</option>)}
        </select>
        <Button size="sm" variant="ghost" icon="chevronLeft" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - limit))}>Previous</Button>
        <Button size="sm" variant="ghost" disabled={offset + limit >= total} onClick={() => setOffset(offset + limit)}>Next <Icon name="chevronRight" size={14} /></Button>
      </div>

      {form && (
        <RowForm table={table} schema={schema} mode={form.mode} row={form.row} columns={data?.columns ?? []} dbRef={dbRef}
          onClose={() => setForm(null)}
          onSave={async values => {
            if (form.mode === 'add') await api.dbsInsert(dbRef, table.schema, table.name, values)
            else await api.dbsUpdate(dbRef, table.schema, table.name, keysOf(form.row!), values)
            toast.success(form.mode === 'add' ? 'Row added.' : 'Row saved.')
            setForm(null); load(); onChanged()
          }} />
      )}
      {importOpen && <ImportDialog table={table} dbRef={dbRef} onClose={() => setImportOpen(false)} onDone={() => { setImportOpen(false); load(); onChanged() }} />}
      {cellView && (
        <Modal title={cellView.col} icon="eye" onClose={() => setCellView(null)} footer={<>
          <Button icon="copy" onClick={() => { navigator.clipboard?.writeText(cellView.value ?? ''); toast.info('Copied.') }}>Copy</Button>
          <Button variant="primary" onClick={() => setCellView(null)}>Close</Button>
        </>}>
          <div className="log" style={{ maxHeight: 420, whiteSpace: 'pre-wrap' }}>{cellView.value ?? 'empty'}</div>
        </Modal>
      )}
    </div>
  )
}

function ExportMenu({ onPick }: { onPick: (f: string) => void }) {
  const [open, setOpen] = useState(false)
  return (
    <div style={{ position: 'relative' }}>
      <Button size="sm" icon="download" onClick={() => setOpen(o => !o)}>Export</Button>
      {open && (
        <>
          <div style={{ position: 'fixed', inset: 0, zIndex: 20 }} onClick={() => setOpen(false)} />
          <div className="menu" style={{ right: 0 }}>
            {[['csv', 'Spreadsheet (CSV)'], ['json', 'JSON'], ['sql', 'Insert statements']].map(([f, label]) => (
              <button key={f} className="menu-item" onClick={() => { setOpen(false); onPick(f) }}>{label}</button>
            ))}
          </div>
        </>
      )}
    </div>
  )
}

function hintFor(c: DbsColumn) {
  const t = c.type.toLowerCase()
  if (/bool|^bit$|tinyint\(1\)/.test(t)) return 'true or false'
  if (/date|time/.test(t)) return t.includes('time') && !/^date$/.test(t) ? 'e.g. 2025-01-31 14:30' : 'e.g. 2025-01-31'
  if (/int|number\(\d+\)$|serial/.test(t)) return 'a whole number'
  if (/dec|num|money|float|double|real/.test(t)) return 'a number, e.g. 12.50'
  if (/uuid|uniqueidentifier/.test(t)) return 'an id like 3f2b…'
  if (/json/.test(t)) return 'JSON'
  return c.maxLength ? `text, up to ${c.maxLength} characters` : 'text'
}

function RowForm({ table, schema, mode, row, columns, dbRef, onClose, onSave }: {
  table: DbsTable; schema: DbSchema; mode: 'add' | 'edit'; row?: (string | null)[]; columns: string[]; dbRef: DbRef
  onClose: () => void; onSave: (values: Record<string, string | null>) => Promise<void>
}) {
  const editable = table.columns.filter(c => !c.computed && !(mode === 'add' && c.identity))
  const [values, setValues] = useState<Record<string, string | null>>(() => Object.fromEntries(editable.map(c => [c.name, mode === 'edit' && row ? row[columns.indexOf(c.name)] : c.nullable || c.default ? null : ''])))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [options, setOptions] = useState<Record<string, { value: string; label: string }[]>>({})

  // Offer the referenced rows as choices for relation columns
  useEffect(() => {
    for (const f of schema.foreignKeys.filter(f => f.schema === table.schema && f.table === table.name && f.columns.length === 1)) {
      const parent = schema.tables.find(t => t.schema === f.refSchema && t.name === f.refTable)
      if (!parent || (parent.rows ?? 0) > 2000) continue
      const label = parent.columns.find(c => /name|title|code|email|description/i.test(c.name) && !c.primaryKey)?.name
      api.dbsRows(dbRef, parent.schema, parent.name, { offset: 0, limit: 500 }).then(r => {
        const vi = r.columns.indexOf(f.refColumns[0]), li = label ? r.columns.indexOf(label) : -1
        setOptions(o => ({ ...o, [f.columns[0]]: r.rows.map(x => ({ value: x[vi] ?? '', label: li >= 0 ? `${x[vi]} · ${x[li] ?? ''}` : x[vi] ?? '' })) }))
      }).catch(() => {})
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const save = async () => {
    setBusy(true); setError(null)
    try {
      const out = mode === 'edit' && row ? Object.fromEntries(Object.entries(values).filter(([k, v]) => v !== row[columns.indexOf(k)])) : values
      await onSave(out)
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  return (
    <Modal title={mode === 'add' ? `Add a row to ${table.name}` : `Edit row in ${table.name}`} icon={mode === 'add' ? 'plus' : 'edit'} size="wide" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" icon="check" loading={busy} onClick={save}>{mode === 'add' ? 'Add row' : 'Save changes'}</Button></>}>
      <div className="col gap-14">
        {error && <Alert kind="error">{error}</Alert>}
        <div className="grid-2">
          {editable.map(c => {
            const v = values[c.name]
            const opts = options[c.name]
            const isBool = /bool|^bit$|tinyint\(1\)/i.test(c.type)
            const long = /text|clob|max\)|json|xml/i.test(c.type) || (c.maxLength ?? 0) > 400
            return (
              <Field key={c.name} label={<span className="row gap-4">{c.primaryKey && <Icon name="key" size={12} />}{c.name}{!c.nullable && !c.default && !c.identity && <span style={{ color: 'var(--danger)' }}>*</span>}<span className="tiny subtle" style={{ fontWeight: 400 }}>{c.type}</span></span>}
                hint={v === null ? (c.default ? `Leave empty to use the default (${c.default})` : c.identity ? 'Filled in automatically' : 'Empty') : hintFor(c)}>
                <div className="row gap-4" style={{ flexWrap: 'nowrap' }}>
                  {opts ? (
                    <select className="select grow" value={v ?? ''} onChange={e => setValues(s => ({ ...s, [c.name]: e.target.value === '' ? null : e.target.value }))}>
                      <option value="">{c.nullable ? '(none)' : 'Choose…'}</option>
                      {opts.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                  ) : isBool ? (
                    <select className="select grow" value={v ?? ''} onChange={e => setValues(s => ({ ...s, [c.name]: e.target.value === '' ? null : e.target.value }))}>
                      <option value="">{c.nullable || c.default ? '(empty)' : 'Choose…'}</option>
                      <option value="true">Yes</option><option value="false">No</option>
                    </select>
                  ) : long ? (
                    <textarea className="textarea grow" rows={3} value={v ?? ''} placeholder={v === null ? 'empty' : ''} onChange={e => setValues(s => ({ ...s, [c.name]: e.target.value }))} />
                  ) : (
                    <input className="input grow" value={v ?? ''} placeholder={v === null ? 'empty' : ''} onChange={e => setValues(s => ({ ...s, [c.name]: e.target.value }))} />
                  )}
                  {(c.nullable || c.default) && v !== null && <button className="btn ghost sm" title="Make empty" onClick={() => setValues(s => ({ ...s, [c.name]: null }))}>∅</button>}
                </div>
              </Field>
            )
          })}
        </div>
      </div>
    </Modal>
  )
}

function parseCsv(text: string): string[][] {
  const rows: string[][] = []
  let row: string[] = [], cell = '', quoted = false
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { cell += '"'; i++ }
      else if (c === '"') quoted = false
      else cell += c
    } else if (c === '"') quoted = true
    else if (c === ',' || c === ';' && !text.slice(0, text.indexOf('\n')).includes(',')) { row.push(cell); cell = '' }
    else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++
      row.push(cell); cell = ''
      if (row.some(x => x !== '')) rows.push(row)
      row = []
    } else cell += c
  }
  row.push(cell)
  if (row.some(x => x !== '')) rows.push(row)
  return rows
}

function ImportDialog({ table, dbRef, onClose, onDone }: { table: DbsTable; dbRef: DbRef; onClose: () => void; onDone: () => void }) {
  const toast = useToast()
  const [rows, setRows] = useState<Record<string, string | null>[] | null>(null)
  const [fileName, setFileName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const read = async (file: File) => {
    setError(null); setFileName(file.name)
    try {
      const text = await file.text()
      if (/\.json$/i.test(file.name) || text.trim().startsWith('[')) {
        const arr = JSON.parse(text)
        if (!Array.isArray(arr)) throw new Error('The JSON file should contain a list of rows.')
        setRows(arr.map((o: Record<string, unknown>) => Object.fromEntries(Object.entries(o).map(([k, v]) => [k, v === null || v === undefined ? null : typeof v === 'object' ? JSON.stringify(v) : String(v)]))))
      } else {
        const [head, ...body] = parseCsv(text)
        if (!head) throw new Error('The file is empty.')
        setRows(body.map(r => Object.fromEntries(head.map((h, i) => [h.trim(), r[i] ?? null]))))
      }
    } catch (e) { setRows(null); setError(e instanceof Error ? e.message : String(e)) }
  }

  const headers = rows?.length ? Object.keys(rows[0]) : []
  const known = headers.filter(h => table.columns.some(c => c.name.toLowerCase() === h.toLowerCase()))
  const unknown = headers.filter(h => !known.includes(h))

  const run = async () => {
    if (!rows) return
    setBusy(true); setError(null)
    try {
      const r = await api.dbsImport(dbRef, table.schema, table.name, rows)
      toast.success(`Imported ${r.affected.toLocaleString()} rows.`)
      onDone()
    } catch (e) { setError(errorText(e)) } finally { setBusy(false) }
  }

  return (
    <Modal title={`Import rows into ${table.name}`} subtitle="From a CSV spreadsheet or a JSON file. Either everything is imported or nothing is." icon="download" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" loading={busy} disabled={!rows?.length || known.length === 0} onClick={run}>Import {rows?.length ? rows.length.toLocaleString() : ''} rows</Button></>}>
      <div className="col gap-14">
        <label className="dropzone">
          <input type="file" accept=".csv,.json,.txt" style={{ display: 'none' }} onChange={e => { const f = e.target.files?.[0]; if (f) read(f) }} />
          <Icon name="fileText" size={22} />
          <span className="strong">{fileName || 'Choose a file'}</span>
          <span className="tiny subtle">The first line of a CSV file must hold the column names.</span>
        </label>
        {error && <Alert kind="error">{error}</Alert>}
        {rows && (
          <div className="col gap-6 small">
            <div><b>{rows.length.toLocaleString()}</b> rows found.</div>
            <div>Columns that will be filled: {known.length ? known.map(k => <span key={k} className="chip" style={{ marginRight: 4 }}>{k}</span>) : <span style={{ color: 'var(--danger)' }}>none match this table</span>}</div>
            {unknown.length > 0 && <div className="subtle">Ignored (not in the table): {unknown.join(', ')}</div>}
          </div>
        )}
      </div>
    </Modal>
  )
}
