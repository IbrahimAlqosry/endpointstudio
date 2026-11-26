import { useEffect, useMemo, useRef, useState } from 'react'
import type { DbSchema, DbsTable } from '../../types'
import { Icon } from '../Icon'
import { Button, Switch } from '../ui'

const W = 230
const HEAD = 34
const ROW = 22
const GAP_X = 90
const GAP_Y = 34
const MAX_COLS = 14

export const tableKey = (t: { schema: string; name: string }) => (t.schema ? `${t.schema}.` : '') + t.name

type Pos = Record<string, { x: number; y: number }>

function heightOf(t: DbsTable, keysOnly: boolean, relCols: Set<string>) {
  const shown = visibleColumns(t, keysOnly, relCols)
  return HEAD + shown.cols.length * ROW + (shown.hidden > 0 ? ROW : 0) + 8
}

function visibleColumns(t: DbsTable, keysOnly: boolean, relCols: Set<string>) {
  const all = keysOnly ? t.columns.filter(c => c.primaryKey || relCols.has(`${tableKey(t)}|${c.name}`)) : t.columns
  const cols = all.slice(0, MAX_COLS)
  return { cols, hidden: t.columns.length - cols.length }
}

/** Places parents to the left of the tables that point at them; unrelated tables go in a grid at the end. */
function autoLayout(s: DbSchema, keysOnly: boolean, relCols: Set<string>): Pos {
  const keys = s.tables.map(tableKey)
  const parents = new Map<string, Set<string>>(keys.map(k => [k, new Set()]))
  const related = new Set<string>()
  for (const f of s.foreignKeys) {
    const child = tableKey({ schema: f.schema, name: f.table }), parent = tableKey({ schema: f.refSchema, name: f.refTable })
    if (child !== parent && parents.has(child) && parents.has(parent)) { parents.get(child)!.add(parent); related.add(child); related.add(parent) }
  }
  const level = new Map<string, number>(keys.map(k => [k, 0]))
  for (let i = 0; i < 30; i++) {
    let changed = false
    for (const k of keys) {
      const lv = Math.max(0, ...[...parents.get(k)!].map(p => (level.get(p) ?? 0) + 1))
      if (lv !== level.get(k) && lv < 30) { level.set(k, lv); changed = true }
    }
    if (!changed) break
  }
  const byKey = new Map(s.tables.map(t => [tableKey(t), t]))
  const pos: Pos = {}
  const maxH = 1600
  let x = 40
  const levels = [...new Set([...related].map(k => level.get(k)!))].sort((a, b) => a - b)
  for (const lv of levels) {
    const items = [...related].filter(k => level.get(k) === lv).sort()
    let y = 40
    for (const k of items) {
      const h = heightOf(byKey.get(k)!, keysOnly, relCols)
      if (y > 40 && y + h > maxH) { x += W + GAP_X; y = 40 }
      pos[k] = { x, y }
      y += h + GAP_Y
    }
    x += W + GAP_X
  }
  // Tables without relations
  const rest = keys.filter(k => !related.has(k)).sort()
  const perRow = Math.max(3, Math.min(8, Math.ceil(Math.sqrt(rest.length))))
  const startX = related.size ? x + 40 : 40
  let rowY = 40, rowH = 0
  rest.forEach((k, i) => {
    if (i > 0 && i % perRow === 0) { rowY += rowH + GAP_Y; rowH = 0 }
    pos[k] = { x: startX + (i % perRow) * (W + 40), y: rowY }
    rowH = Math.max(rowH, heightOf(byKey.get(k)!, keysOnly, relCols))
  })
  return pos
}

export function ErDiagram({ schema, storageKey, selected, onSelect, onOpen }: {
  schema: DbSchema
  storageKey: string
  selected: string | null
  onSelect: (key: string | null) => void
  onOpen: (key: string) => void
}) {
  const [keysOnly, setKeysOnly] = useState(() => schema.tables.length > 30)
  const [filter, setFilter] = useState('')
  const [schemaFilter, setSchemaFilter] = useState('')
  const relCols = useMemo(() => {
    const set = new Set<string>()
    for (const f of schema.foreignKeys) {
      f.columns.forEach(c => set.add(`${tableKey({ schema: f.schema, name: f.table })}|${c}`))
      f.refColumns.forEach(c => set.add(`${tableKey({ schema: f.refSchema, name: f.refTable })}|${c}`))
    }
    return set
  }, [schema])
  const [pos, setPos] = useState<Pos>(() => {
    try { const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null'); if (saved) return saved } catch { /* ignore */ }
    return {}
  })
  const [view, setView] = useState({ x: 0, y: 0, k: 1 })
  const [hover, setHover] = useState<string | null>(null)
  const svgRef = useRef<SVGSVGElement>(null)
  const drag = useRef<{ kind: 'pan' | 'box'; key?: string; sx: number; sy: number; ox: number; oy: number; moved: boolean } | null>(null)

  // Fill in positions for tables that don't have one yet
  const layout = useMemo(() => autoLayout(schema, keysOnly, relCols), [schema, keysOnly, relCols])
  const P = (k: string) => pos[k] ?? layout[k] ?? { x: 0, y: 0 }
  useEffect(() => { try { localStorage.setItem(storageKey, JSON.stringify(pos)) } catch { /* ignore */ } }, [pos, storageKey])

  const tables = schema.tables.filter(t => !schemaFilter || t.schema === schemaFilter)
  const visible = new Set(tables.map(tableKey))
  const q = filter.trim().toLowerCase()
  const match = (t: DbsTable) => !q || t.name.toLowerCase().includes(q) || t.columns.some(c => c.name.toLowerCase().includes(q))
  const focus = hover ?? selected
  const neighbours = useMemo(() => {
    const set = new Set<string>()
    if (!focus) return set
    for (const f of schema.foreignKeys) {
      const a = tableKey({ schema: f.schema, name: f.table }), b = tableKey({ schema: f.refSchema, name: f.refTable })
      if (a === focus) set.add(b)
      if (b === focus) set.add(a)
    }
    return set
  }, [focus, schema])

  const fit = () => {
    const el = svgRef.current
    if (!el || tables.length === 0) return
    const xs = tables.map(t => P(tableKey(t)))
    const minX = Math.min(...xs.map(p => p.x)), minY = Math.min(...xs.map(p => p.y))
    const maxX = Math.max(...xs.map(p => p.x + W))
    const maxY = Math.max(...tables.map(t => P(tableKey(t)).y + heightOf(t, keysOnly, relCols)))
    const r = el.getBoundingClientRect()
    const k = Math.min(1.2, Math.max(0.15, Math.min(r.width / (maxX - minX + 80), r.height / (maxY - minY + 80))))
    setView({ k, x: -minX * k + 40 + (r.width - (maxX - minX + 80) * k) / 2, y: -minY * k + 40 })
  }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { const t = setTimeout(fit, 30); return () => clearTimeout(t) }, [schema, schemaFilter])

  const zoomAt = (factor: number, cx?: number, cy?: number) => {
    const r = svgRef.current?.getBoundingClientRect()
    const px = cx ?? (r ? r.width / 2 : 0), py = cy ?? (r ? r.height / 2 : 0)
    setView(v => {
      const k = Math.min(2.5, Math.max(0.1, v.k * factor))
      return { k, x: px - ((px - v.x) / v.k) * k, y: py - ((py - v.y) / v.k) * k }
    })
  }

  useEffect(() => {
    const el = svgRef.current
    if (!el) return
    const onWheel = (e: WheelEvent) => {
      e.preventDefault()
      const r = el.getBoundingClientRect()
      if (e.ctrlKey || e.metaKey || Math.abs(e.deltaY) > 40 && !e.shiftKey) zoomAt(e.deltaY < 0 ? 1.12 : 1 / 1.12, e.clientX - r.left, e.clientY - r.top)
      else setView(v => ({ ...v, x: v.x - e.deltaX, y: v.y - e.deltaY }))
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [])

  const onPointerDown = (e: React.PointerEvent, key?: string) => {
    e.stopPropagation()
    ;(e.target as Element).setPointerCapture?.(e.pointerId)
    const p = key ? P(key) : { x: view.x, y: view.y }
    drag.current = { kind: key ? 'box' : 'pan', key, sx: e.clientX, sy: e.clientY, ox: p.x, oy: p.y, moved: false }
  }
  const onPointerMove = (e: React.PointerEvent) => {
    const d = drag.current
    if (!d) return
    const dx = e.clientX - d.sx, dy = e.clientY - d.sy
    if (Math.abs(dx) + Math.abs(dy) > 3) d.moved = true
    if (d.kind === 'pan') setView(v => ({ ...v, x: d.ox + dx, y: d.oy + dy }))
    else setPos(p => ({ ...p, [d.key!]: { x: Math.round(d.ox + dx / view.k), y: Math.round(d.oy + dy / view.k) } }))
  }
  const onPointerUp = () => {
    const d = drag.current
    drag.current = null
    if (d && !d.moved) onSelect(d.kind === 'box' ? d.key! : null)
  }

  const rowY = (t: DbsTable, col: string) => {
    const { cols } = visibleColumns(t, keysOnly, relCols)
    const i = cols.findIndex(c => c.name === col)
    return HEAD + (i < 0 ? 0 : i) * ROW + ROW / 2 + 4
  }

  const byKey = new Map(schema.tables.map(t => [tableKey(t), t]))
  const edges = schema.foreignKeys.flatMap(f => {
    const a = tableKey({ schema: f.schema, name: f.table }), b = tableKey({ schema: f.refSchema, name: f.refTable })
    const ta = byKey.get(a), tb = byKey.get(b)
    if (!ta || !tb || !visible.has(a) || !visible.has(b)) return []
    const pa = P(a), pb = P(b)
    const y1 = pa.y + rowY(ta, f.columns[0]), y2 = pb.y + rowY(tb, f.refColumns[0])
    let path: string
    let x1: number, x2: number
    if (a === b) {
      x1 = pa.x + W; x2 = pa.x + W
      path = `M ${x1} ${y1} C ${x1 + 50} ${y1}, ${x2 + 50} ${y2}, ${x2} ${y2}`
    } else {
      const aLeft = pa.x + W / 2 < pb.x + W / 2
      x1 = aLeft ? pa.x + W : pa.x
      x2 = aLeft ? pb.x : pb.x + W
      if (Math.abs(pa.x - pb.x) < W) { x1 = pa.x + W; x2 = pb.x + W }
      const c = Math.max(40, Math.abs(x2 - x1) / 2)
      const s1 = x1 === pa.x + W ? 1 : -1, s2 = x2 === pb.x + W ? 1 : -1
      path = `M ${x1} ${y1} C ${x1 + s1 * c} ${y1}, ${x2 + s2 * c} ${y2}, ${x2} ${y2}`
    }
    const active = focus != null && (a === focus || b === focus)
    return [{ f, path, x1, y1, x2, y2, active, dim: focus != null && !active }]
  })

  const exportSvg = () => {
    const el = svgRef.current
    if (!el) return
    const clone = el.cloneNode(true) as SVGSVGElement
    const cs = getComputedStyle(document.documentElement)
    let css = ''
    for (const v of ['--bg', '--bg-elev', '--bg-sunken', '--border', '--border-strong', '--text', '--text-2', '--text-3', '--accent', '--accent-soft', '--accent-text', '--warning', '--font', '--mono']) css += `${v}:${cs.getPropertyValue(v)};`
    clone.setAttribute('style', css + 'background:' + cs.getPropertyValue('--bg'))
    clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg')
    const blob = new Blob([new XMLSerializer().serializeToString(clone)], { type: 'image/svg+xml' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${schema.database || 'database'}-diagram.svg`
    a.click()
    setTimeout(() => URL.revokeObjectURL(a.href), 2000)
  }

  return (
    <div className="er">
      <div className="er-toolbar">
        <div className="search" style={{ maxWidth: 280, minWidth: 160 }}><Icon name="search" size={16} /><input className="input" placeholder="Find a table or column…" value={filter} onChange={e => setFilter(e.target.value)} /></div>
        {schema.schemas.length > 1 && (
          <select className="select" style={{ width: 'auto' }} value={schemaFilter} onChange={e => setSchemaFilter(e.target.value)}>
            <option value="">All schemas</option>
            {schema.schemas.map(s => <option key={s} value={s}>{s}</option>)}
          </select>
        )}
        <Switch checked={keysOnly} onChange={setKeysOnly} label="Keys only" />
        <div className="grow" />
        <Button size="sm" variant="ghost" title="Zoom out" onClick={() => zoomAt(1 / 1.25)}>−</Button>
        <span className="tiny subtle" style={{ minWidth: 38, textAlign: 'center' }}>{Math.round(view.k * 100)}%</span>
        <Button size="sm" variant="ghost" onClick={() => zoomAt(1.25)}>+</Button>
        <Button size="sm" variant="ghost" onClick={fit}>Fit</Button>
        <Button size="sm" variant="ghost" icon="wand" onClick={() => { setPos({}); setTimeout(fit, 30) }}>Arrange</Button>
        <Button size="sm" variant="ghost" icon="download" onClick={exportSvg}>Save picture</Button>
      </div>
      <svg ref={svgRef} className="er-canvas" onPointerDown={e => onPointerDown(e)} onPointerMove={onPointerMove} onPointerUp={onPointerUp}>
        <defs>
          <pattern id="er-grid" width="24" height="24" patternUnits="userSpaceOnUse" patternTransform={`translate(${view.x % 24} ${view.y % 24})`}>
            <circle cx="1" cy="1" r="1" fill="var(--border)" />
          </pattern>
          <marker id="er-one" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="12" markerHeight="12" orient="auto-start-reverse">
            <path d="M 2 1 L 2 11 M 6 1 L 6 11" stroke="context-stroke" strokeWidth="1.5" fill="none" />
          </marker>
          <marker id="er-many" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="12" markerHeight="12" orient="auto-start-reverse">
            <path d="M 11 1 L 1 6 L 11 11 M 1 1 L 1 11" stroke="context-stroke" strokeWidth="1.5" fill="none" />
          </marker>
        </defs>
        <rect width="100%" height="100%" fill="url(#er-grid)" />
        <g transform={`translate(${view.x} ${view.y}) scale(${view.k})`}>
          {edges.map(({ f, path, active, dim }) => (
            <path key={`${tableKey({ schema: f.schema, name: f.table })}|${f.name}`} d={path} fill="none" className={`er-edge${active ? ' active' : ''}${dim ? ' dim' : ''}`}
              markerStart="url(#er-many)" markerEnd="url(#er-one)">
              <title>{`${f.table}.${f.columns.join(', ')} → ${f.refTable}.${f.refColumns.join(', ')}${f.onDelete !== 'NO ACTION' ? ` (on delete ${f.onDelete.toLowerCase()})` : ''}`}</title>
            </path>
          ))}
          {tables.map(t => {
            const k = tableKey(t)
            const p = P(k)
            const { cols, hidden } = visibleColumns(t, keysOnly, relCols)
            const h = heightOf(t, keysOnly, relCols)
            const dim = (q && !match(t)) || (focus != null && k !== focus && !neighbours.has(k))
            return (
              <g key={k} transform={`translate(${p.x} ${p.y})`} className={`er-table${k === selected ? ' selected' : ''}${dim ? ' dim' : ''}`}
                onPointerDown={e => onPointerDown(e, k)} onDoubleClick={() => onOpen(k)}
                onPointerEnter={() => setHover(k)} onPointerLeave={() => setHover(null)}>
                <rect width={W} height={h} rx="10" className="er-box" />
                <path d={`M 0 10 a 10 10 0 0 1 10 -10 h ${W - 20} a 10 10 0 0 1 10 10 v ${HEAD - 10} h ${-W} z`} className="er-head" />
                <text x="12" y="21" className="er-title">{t.name.length > 24 ? t.name.slice(0, 23) + '…' : t.name}</text>
                {t.schema && schema.capabilities.schemas && <text x={W - 10} y="21" textAnchor="end" className="er-schema">{t.schema}</text>}
                {cols.map((c, i) => {
                  const isFk = relCols.has(`${k}|${c.name}`) && !c.primaryKey
                  const y = HEAD + i * ROW + 4
                  return (
                    <g key={c.name}>
                      {(q && c.name.toLowerCase().includes(q)) && <rect x="2" y={y} width={W - 4} height={ROW} className="er-hit" />}
                      <text x="12" y={y + 15} className={`er-col${c.primaryKey ? ' pk' : ''}`}>{c.primaryKey ? '🔑 ' : isFk ? '↗ ' : ''}{c.name.length > 20 ? c.name.slice(0, 19) + '…' : c.name}{c.nullable ? '' : ' *'}</text>
                      <text x={W - 10} y={y + 15} textAnchor="end" className="er-type">{c.type.length > 14 ? c.type.slice(0, 13) + '…' : c.type}</text>
                    </g>
                  )
                })}
                {hidden > 0 && <text x="12" y={HEAD + cols.length * ROW + 19} className="er-more">+{hidden} more column{hidden === 1 ? '' : 's'}</text>}
                <title>{`${tableKey(t)} · ${t.columns.length} columns${t.rows != null ? ` · about ${t.rows.toLocaleString()} rows` : ''}\nDouble-click to open`}</title>
              </g>
            )
          })}
        </g>
      </svg>
      <div className="er-legend tiny subtle">
        <span>🔑 key</span><span>↗ points to another table</span><span>* required</span><span>Drag tables to move them · drag the background to pan · scroll to zoom · double-click to open</span>
      </div>
    </div>
  )
}
