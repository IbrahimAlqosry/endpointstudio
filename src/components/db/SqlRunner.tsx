import { useEffect, useState } from 'react'
import { api } from '../../api'
import type { DbRef, DbSchema, SqlResult } from '../../types'
import { Icon } from '../Icon'
import { Alert, Button, errorText, useConfirm } from '../ui'

export function SqlRunner({ dbRef, schema, storageKey, initial, onChanged }: { dbRef: DbRef; schema: DbSchema; storageKey: string; initial?: string; onChanged: () => void }) {
  const confirm = useConfirm()
  const [sql, setSql] = useState(() => initial ?? (() => { try { return localStorage.getItem(storageKey + ':draft') ?? '' } catch { return '' } })())
  const [result, setResult] = useState<SqlResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [active, setActive] = useState(0)
  const [history, setHistory] = useState<string[]>(() => { try { return JSON.parse(localStorage.getItem(storageKey + ':history') ?? '[]') } catch { return [] } })
  const [showHistory, setShowHistory] = useState(false)

  useEffect(() => { if (initial) setSql(initial) }, [initial])
  useEffect(() => { try { localStorage.setItem(storageKey + ':draft', sql) } catch { /* ignore */ } }, [sql, storageKey])

  const run = async (dryRun: boolean, confirmed = false) => {
    if (!sql.trim()) return
    setBusy(true); setError(null)
    try {
      const r = await api.dbsSql(dbRef, sql, dryRun, confirmed)
      if (r.needsConfirm) {
        setBusy(false)
        if (await confirm({ title: 'Are you sure?', message: r.needsConfirm, confirmText: 'Run it', danger: true })) return run(false, true)
        return
      }
      setResult(r); setActive(0)
      const h = [sql, ...history.filter(x => x !== sql)].slice(0, 30)
      setHistory(h)
      try { localStorage.setItem(storageKey + ':history', JSON.stringify(h)) } catch { /* ignore */ }
      if (!dryRun && r.results.some(x => x.columns.length === 0)) onChanged()
    } catch (e) { setError(errorText(e)); setResult(null) } finally { setBusy(false) }
  }

  const q = (n: string) => schema.provider === 'SqlServer' ? `[${n}]` : schema.provider === 'MySql' ? `\`${n}\`` : /^[a-z_][a-z0-9_]*$/.test(n) || (schema.provider === 'Oracle' && /^[A-Z_][A-Z0-9_$#]*$/.test(n)) ? n : `"${n}"`
  const first = schema.tables[0]
  const name = first ? (first.schema && schema.capabilities.schemas ? `${q(first.schema)}.${q(first.name)}` : q(first.name)) : 'my_table'
  const top = (n: string) => schema.provider === 'SqlServer' ? `SELECT TOP 100 * FROM ${n}` : schema.provider === 'Oracle' ? `SELECT * FROM ${n} FETCH FIRST 100 ROWS ONLY` : `SELECT * FROM ${n} LIMIT 100`
  const snippets: [string, string][] = [
    ['First 100 rows', top(name)],
    ['Count rows', `SELECT COUNT(*) AS total FROM ${name}`],
    ['Group and count', `SELECT ${first?.columns[1] ? q(first.columns[1].name) : 'column'}, COUNT(*) AS total\nFROM ${name}\nGROUP BY ${first?.columns[1] ? q(first.columns[1].name) : 'column'}\nORDER BY total DESC`],
  ]
  const set = result?.results[active]

  return (
    <div className="col gap-10">
      <div className="row wrap gap-6">
        <span className="small subtle">Start from:</span>
        {snippets.map(([label, text]) => <button key={label} className="chip" onClick={() => setSql(text)}>{label}</button>)}
        <div className="grow" />
        {history.length > 0 && <Button size="sm" variant="ghost" icon="history" onClick={() => setShowHistory(s => !s)}>Recent ({history.length})</Button>}
      </div>
      {showHistory && (
        <div className="card" style={{ maxHeight: 220, overflow: 'auto' }}>
          {history.map((h, i) => (
            <button key={i} className="history-item mono" onClick={() => { setSql(h); setShowHistory(false) }}>{h.length > 200 ? h.slice(0, 200) + '…' : h}</button>
          ))}
        </div>
      )}
      <textarea className="textarea code sql-editor" spellCheck={false} value={sql} onChange={e => setSql(e.target.value)} rows={9}
        placeholder="Write a query, e.g. SELECT * FROM Customers WHERE City = 'Cairo'"
        onKeyDown={e => {
          if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); run(false) }
          if (e.key === 'Tab') { e.preventDefault(); const t = e.currentTarget, s = t.selectionStart; setSql(sql.slice(0, s) + '  ' + sql.slice(t.selectionEnd)); setTimeout(() => t.setSelectionRange(s + 2, s + 2)) }
        }} />
      <div className="row wrap gap-6">
        <Button variant="primary" icon="play" loading={busy} disabled={!sql.trim()} onClick={() => run(false)}>Run</Button>
        <Button icon="eye" disabled={busy || !sql.trim()} onClick={() => run(true)} title="Runs everything, shows the results, then undoes all changes">Try without saving</Button>
        <span className="tiny subtle">Ctrl+Enter runs. {schema.provider === 'SqlServer' ? 'Separate batches with GO.' : 'Separate statements with ;'}</span>
        <div className="grow" />
        {result && <span className="small subtle">{result.durationMs.toLocaleString()} ms{result.rolledBack ? ' · changes were undone' : ''}</span>}
      </div>
      {error && <Alert kind="error">{error}</Alert>}
      {result && result.rolledBack && <Alert kind="info">This was a trial run. Everything it changed has been undone.</Alert>}
      {result && result.results.length > 1 && (
        <div className="tabs" style={{ marginTop: 0 }}>
          {result.results.map((r, i) => (
            <button key={i} className={`tab${active === i ? ' active' : ''}`} onClick={() => setActive(i)}>
              {r.columns.length ? `Result ${i + 1} (${r.rows.length}${r.truncated ? '+' : ''})` : `Change ${i + 1}`}
            </button>
          ))}
        </div>
      )}
      {set && set.columns.length === 0 && (
        <div className="card pad row gap-8"><Icon name="checkCircle" size={18} /><div><div className="strong">{set.affected != null && set.affected >= 0 ? `${set.affected.toLocaleString()} row${set.affected === 1 ? '' : 's'} affected` : 'Done'}</div>{set.message && <div className="mono tiny subtle">{set.message}</div>}</div></div>
      )}
      {set && set.columns.length > 0 && (
        <>
          <div className="table-wrap grid-wrap">
            <table className="table data-grid">
              <thead><tr>{set.columns.map((c, i) => <th key={i}>{c || <span className="subtle">(no name)</span>}</th>)}</tr></thead>
              <tbody>
                {set.rows.map((r, i) => <tr key={i}>{r.map((v, j) => <td key={j} className={v === null ? 'null' : ''}><span className="cell" title={v ?? 'empty'}>{v === null ? 'empty' : v.length > 120 ? v.slice(0, 120) + '…' : v}</span></td>)}</tr>)}
              </tbody>
            </table>
            {set.rows.length === 0 && <div className="empty small subtle">No rows.</div>}
          </div>
          <div className="small subtle">{set.rows.length.toLocaleString()} rows{set.truncated ? ' (only the first 1,000 are shown)' : ''}</div>
        </>
      )}
    </div>
  )
}
