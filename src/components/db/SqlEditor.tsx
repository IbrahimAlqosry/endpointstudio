import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { DbSchema, DbsTable } from '../../types'
import { Icon } from '../Icon'

const KEYWORDS = [
  'SELECT', 'FROM', 'WHERE', 'AND', 'OR', 'NOT', 'IN', 'IS', 'NULL', 'LIKE', 'BETWEEN', 'EXISTS', 'AS', 'DISTINCT', 'ORDER BY', 'GROUP BY', 'HAVING',
  'JOIN', 'INNER JOIN', 'LEFT JOIN', 'RIGHT JOIN', 'FULL JOIN', 'CROSS JOIN', 'ON', 'UNION', 'UNION ALL', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END',
  'INSERT INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE FROM', 'CREATE TABLE', 'ALTER TABLE', 'DROP TABLE', 'ADD', 'COLUMN', 'PRIMARY KEY', 'FOREIGN KEY',
  'REFERENCES', 'DEFAULT', 'CONSTRAINT', 'UNIQUE', 'CHECK', 'INDEX', 'CREATE INDEX', 'CREATE VIEW', 'BEGIN', 'COMMIT', 'ROLLBACK', 'ASC', 'DESC',
  'COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'COALESCE', 'CAST', 'UPPER', 'LOWER', 'TRIM', 'LENGTH', 'ROUND', 'ABS', 'NOW', 'WITH', 'OVER', 'PARTITION BY',
  'ROW_NUMBER', 'GRANT', 'REVOKE', 'TRUNCATE TABLE', 'RETURNS', 'RETURN', 'DECLARE', 'IF', 'WHILE', 'PROCEDURE', 'FUNCTION', 'TRIGGER',
]
const EXTRA: Record<string, string[]> = {
  SqlServer: ['TOP', 'GETDATE', 'ISNULL', 'NEWID', 'IDENTITY', 'NVARCHAR', 'OFFSET', 'FETCH NEXT', 'ROWS ONLY', 'EXEC', 'GO', 'CREATE OR ALTER', 'SET NOCOUNT ON', 'OUTPUT', 'MERGE', 'TRY_CAST', 'DATEADD', 'DATEDIFF', 'STRING_AGG'],
  PostgreSql: ['LIMIT', 'OFFSET', 'ILIKE', 'RETURNING', 'SERIAL', 'NOW()', 'CURRENT_DATE', 'CREATE OR REPLACE', 'LANGUAGE plpgsql', 'ON CONFLICT', 'DO NOTHING', 'STRING_AGG', 'JSONB', 'GENERATE_SERIES', 'CALL'],
  MySql: ['LIMIT', 'OFFSET', 'AUTO_INCREMENT', 'IFNULL', 'NOW()', 'CURDATE()', 'GROUP_CONCAT', 'ON DUPLICATE KEY UPDATE', 'CALL', 'DELIMITER'],
  Oracle: ['ROWNUM', 'FETCH FIRST', 'ROWS ONLY', 'NVL', 'SYSDATE', 'SYSTIMESTAMP', 'DUAL', 'VARCHAR2', 'NUMBER', 'CREATE OR REPLACE', 'MERGE INTO', 'LISTAGG', 'TO_CHAR', 'TO_DATE'],
  Sqlite: ['LIMIT', 'OFFSET', 'AUTOINCREMENT', 'IFNULL', 'DATETIME', 'STRFTIME', 'GROUP_CONCAT', 'PRAGMA', 'RETURNING'],
}

type Suggestion = { label: string; insert: string; kind: 'keyword' | 'table' | 'view' | 'column' | 'routine'; detail?: string }

function quoteIfNeeded(name: string, provider: string) {
  const plain = provider === 'Oracle' ? /^[A-Z_][A-Z0-9_$#]*$/.test(name) : /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) && (provider !== 'PostgreSql' || name === name.toLowerCase())
  if (plain) return name
  return provider === 'SqlServer' ? `[${name}]` : provider === 'MySql' ? `\`${name}\`` : `"${name}"`
}
const unquote = (s: string) => s.replace(/^[[`"]|[\]`"]$/g, '')

/** Where the caret is on screen, measured with a hidden copy of the textarea. */
function caretPosition(ta: HTMLTextAreaElement, index: number) {
  const div = document.createElement('div')
  const style = getComputedStyle(ta)
  for (const p of ['fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'paddingTop', 'paddingLeft', 'paddingRight', 'paddingBottom', 'borderTopWidth', 'borderLeftWidth', 'boxSizing', 'tabSize', 'whiteSpace', 'wordWrap'] as const)
    (div.style as unknown as Record<string, string>)[p] = (style as unknown as Record<string, string>)[p]
  div.style.position = 'absolute'; div.style.visibility = 'hidden'; div.style.whiteSpace = 'pre-wrap'; div.style.wordWrap = 'break-word'
  div.style.width = ta.clientWidth + 'px'
  div.textContent = ta.value.slice(0, index)
  const span = document.createElement('span')
  span.textContent = '​'
  div.appendChild(span)
  document.body.appendChild(div)
  const top = span.offsetTop - ta.scrollTop, left = span.offsetLeft - ta.scrollLeft
  const lh = parseFloat(style.lineHeight) || 18
  div.remove()
  return { top: top + lh + 2, left }
}

export function SqlEditor({ value, onChange, schema, rows = 9, onRun, placeholder, autoFocus, minHeight }: {
  value: string
  onChange: (v: string) => void
  schema: DbSchema | null
  rows?: number
  onRun?: () => void
  placeholder?: string
  autoFocus?: boolean
  minHeight?: number
}) {
  const ta = useRef<HTMLTextAreaElement>(null)
  const [menu, setMenu] = useState<{ items: Suggestion[]; start: number; end: number; top: number; left: number } | null>(null)
  const [active, setActive] = useState(0)
  const pending = useRef<number | null>(null)
  const provider = schema?.provider ?? 'SqlServer'

  const keywords = useMemo(() => [...KEYWORDS, ...(EXTRA[provider] ?? [])], [provider])
  const byName = useMemo(() => {
    const m = new Map<string, DbsTable>()
    for (const t of schema?.tables ?? []) { m.set(t.name.toLowerCase(), t); if (t.schema) m.set(`${t.schema}.${t.name}`.toLowerCase(), t) }
    return m
  }, [schema])

  // Restores the caret after an inserted suggestion
  useLayoutEffect(() => {
    if (pending.current != null && ta.current) { ta.current.setSelectionRange(pending.current, pending.current); pending.current = null }
  })

  /** Tables named in the query, with their aliases: FROM orders o JOIN customers AS c … */
  const tablesInQuery = (text: string) => {
    const found = new Map<string, DbsTable>()
    const re = /\b(?:FROM|JOIN|UPDATE|INTO)\s+([\w$#."[\]`]+)(?:\s+(?:AS\s+)?([A-Za-z_]\w*))?/gi
    let m: RegExpExecArray | null
    while ((m = re.exec(text))) {
      const name = m[1].split('.').map(unquote).join('.').toLowerCase()
      const t = byName.get(name) ?? byName.get(name.split('.').pop()!)
      if (!t) continue
      found.set(t.name.toLowerCase(), t)
      const alias = m[2]?.toLowerCase()
      if (alias && !/^(where|on|join|inner|left|right|full|cross|group|order|set|values|limit|union|having)$/.test(alias)) found.set(alias, t)
    }
    return found
  }

  const suggest = (force = false) => {
    const el = ta.current
    if (!el || !schema) return
    const text = el.value
    const caret = el.selectionStart
    const before = text.slice(0, caret)
    const word = /[\w$#."[\]`]*$/.exec(before)?.[0] ?? ''
    const dot = word.lastIndexOf('.')
    let items: Suggestion[] = []
    let start = caret - word.length
    if (dot >= 0) {
      // table.column or alias.column
      const owner = unquote(word.slice(0, dot).split('.').pop() ?? '').toLowerCase()
      const part = unquote(word.slice(dot + 1)).toLowerCase()
      start = caret - (word.length - dot - 1)
      const t = tablesInQuery(text).get(owner) ?? byName.get(owner)
      if (t) items = t.columns.filter(c => c.name.toLowerCase().startsWith(part))
        .map(c => ({ label: c.name, insert: quoteIfNeeded(c.name, provider), kind: 'column' as const, detail: c.type + (c.primaryKey ? ' · key' : '') }))
      // schema.table
      if (items.length === 0 && schema.schemas.some(s => s.toLowerCase() === owner))
        items = [...schema.tables, ...schema.views].filter(x => x.schema.toLowerCase() === owner && x.name.toLowerCase().startsWith(part))
          .map(x => ({ label: x.name, insert: quoteIfNeeded(x.name, provider), kind: 'columns' in x ? 'table' as const : 'view' as const }))
    } else {
      const part = unquote(word).toLowerCase()
      if (!force && part.length < 1) { setMenu(null); return }
      const inQuery = tablesInQuery(text)
      const prev = /(\w+)\s*$/.exec(before.slice(0, start))?.[1]?.toUpperCase() ?? ''
      const wantsTable = /^(FROM|JOIN|INTO|UPDATE|TABLE|EXEC|CALL)$/.test(prev)
      const cols: Suggestion[] = []
      const seen = new Set<string>()
      for (const t of new Set(inQuery.values()))
        for (const c of t.columns)
          if (c.name.toLowerCase().startsWith(part) && !seen.has(c.name.toLowerCase())) { seen.add(c.name.toLowerCase()); cols.push({ label: c.name, insert: quoteIfNeeded(c.name, provider), kind: 'column', detail: `${t.name} · ${c.type}` }) }
      const tbls: Suggestion[] = [
        ...schema.tables.filter(t => t.name.toLowerCase().startsWith(part)).map(t => ({ label: t.name, insert: (schema.capabilities.schemas && t.schema && !['dbo', 'public'].includes(t.schema) ? quoteIfNeeded(t.schema, provider) + '.' : '') + quoteIfNeeded(t.name, provider), kind: 'table' as const, detail: t.schema || undefined })),
        ...schema.views.filter(v => v.name.toLowerCase().startsWith(part)).map(v => ({ label: v.name, insert: quoteIfNeeded(v.name, provider), kind: 'view' as const, detail: v.schema || undefined })),
        ...schema.routines.filter(r => r.name.toLowerCase().startsWith(part)).map(r => ({ label: r.name, insert: quoteIfNeeded(r.name, provider), kind: 'routine' as const, detail: r.kind })),
      ]
      const kws: Suggestion[] = keywords.filter(k => k.toLowerCase().startsWith(part) && k.toLowerCase() !== part).map(k => ({ label: k, insert: k, kind: 'keyword' as const }))
      items = wantsTable ? [...tbls, ...cols, ...kws] : [...cols, ...tbls, ...kws]
    }
    items = items.slice(0, 40)
    if (items.length === 0 || (items.length === 1 && items[0].insert === word)) { setMenu(null); return }
    const pos = caretPosition(el, start)
    setMenu({ items, start, end: caret, top: Math.min(pos.top, el.clientHeight + 4), left: Math.min(pos.left, el.clientWidth - 260) })
    setActive(0)
  }

  const accept = (s: Suggestion) => {
    if (!menu) return
    const text = ta.current?.value ?? value
    const next = text.slice(0, menu.start) + s.insert + text.slice(menu.end)
    pending.current = menu.start + s.insert.length
    onChange(next)
    setMenu(null)
  }

  useEffect(() => { if (!value) setMenu(null) }, [value])

  return (
    <div className="sql-editor-wrap">
      <textarea ref={ta} className="textarea code sql-editor" spellCheck={false} value={value} rows={rows} placeholder={placeholder} autoFocus={autoFocus}
        style={minHeight ? { minHeight } : undefined}
        onChange={e => { onChange(e.target.value); requestAnimationFrame(() => suggest()) }}
        onBlur={() => setTimeout(() => setMenu(null), 150)}
        onClick={() => setMenu(null)}
        onKeyDown={e => {
          if (menu) {
            if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => (a + 1) % menu.items.length); return }
            if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => (a - 1 + menu.items.length) % menu.items.length); return }
            if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); accept(menu.items[active]); return }
            if (e.key === 'Escape') { e.preventDefault(); setMenu(null); return }
          }
          if (e.key === ' ' && e.ctrlKey) { e.preventDefault(); suggest(true); return }
          if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); onRun?.(); return }
          if (e.key === 'Tab') {
            e.preventDefault()
            const t = e.currentTarget, s = t.selectionStart
            pending.current = s + 2
            onChange(value.slice(0, s) + '  ' + value.slice(t.selectionEnd))
          }
        }} />
      {menu && (
        <div className="sql-suggest" style={{ top: menu.top, left: Math.max(4, menu.left) }}>
          {menu.items.map((s, i) => (
            <button key={s.kind + s.label + i} className={`sql-suggest-item${i === active ? ' active' : ''}`} onMouseDown={e => { e.preventDefault(); accept(s) }} onMouseMove={() => setActive(i)}>
              <Icon name={s.kind === 'column' ? 'list' : s.kind === 'table' ? 'table' : s.kind === 'view' ? 'eye' : s.kind === 'routine' ? 'zap' : 'code'} size={13} />
              <span className="grow truncate">{s.label}</span>
              {s.detail && <span className="tiny subtle truncate" style={{ maxWidth: 140 }}>{s.detail}</span>}
            </button>
          ))}
          <div className="sql-suggest-foot tiny subtle">Enter or Tab to insert · Ctrl+Space to show</div>
        </div>
      )}
    </div>
  )
}
