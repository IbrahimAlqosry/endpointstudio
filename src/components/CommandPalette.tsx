import { useEffect, useMemo, useRef, useState } from 'react'
import { onPaletteChange, paletteItems, type PaletteItem } from '../palette'
import { Icon } from './Icon'

/** Ctrl+K: find and do anything (projects, pages, endpoints, tables, actions). */
export function CommandPalette({ base }: { base: PaletteItem[] }) {
  const [open, setOpen] = useState(false)
  const [q, setQ] = useState('')
  const [active, setActive] = useState(0)
  const [, bump] = useState(0)
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => onPaletteChange(() => bump(n => n + 1)), [])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setOpen(o => !o); setQ(''); setActive(0) }
      else if (e.key === 'Escape') setOpen(false)
    }
    const onOpen = () => { setOpen(true); setQ(''); setActive(0) }
    window.addEventListener('keydown', onKey)
    window.addEventListener('sw-palette', onOpen)
    return () => { window.removeEventListener('keydown', onKey); window.removeEventListener('sw-palette', onOpen) }
  }, [])

  const results = useMemo(() => {
    const all = [...paletteItems(), ...base]
    const words = q.toLowerCase().split(/\s+/).filter(Boolean)
    if (words.length === 0) return all.slice(0, 60)
    const scored: { item: PaletteItem; score: number }[] = []
    for (const item of all) {
      const title = item.title.toLowerCase()
      const hay = `${title} ${item.subtitle ?? ''} ${item.keywords ?? ''} ${item.group}`.toLowerCase()
      if (!words.every(w => hay.includes(w))) continue
      let score = 0
      for (const w of words) score += title.startsWith(w) ? 4 : title.includes(w) ? 2 : 1
      scored.push({ item, score })
    }
    return scored.sort((a, b) => b.score - a.score).slice(0, 60).map(s => s.item)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, base, open])

  useEffect(() => { listRef.current?.querySelector('.palette-item.active')?.scrollIntoView({ block: 'nearest' }) }, [active])
  if (!open) return null

  const run = (item: PaletteItem) => { setOpen(false); item.run() }
  let lastGroup = ''
  return (
    <div className="overlay palette-overlay" onMouseDown={e => { if (e.target === e.currentTarget) setOpen(false) }}>
      <div className="palette" role="dialog" aria-label="Command palette">
        <div className="palette-search">
          <Icon name="search" size={18} />
          <input autoFocus placeholder="Search projects, endpoints, tables, actions…" value={q}
            onChange={e => { setQ(e.target.value); setActive(0) }}
            onKeyDown={e => {
              if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(results.length - 1, a + 1)) }
              if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(0, a - 1)) }
              if (e.key === 'Enter' && results[active]) run(results[active])
            }} />
          <kbd>Esc</kbd>
        </div>
        <div className="palette-list" ref={listRef}>
          {results.length === 0 && <div className="small subtle" style={{ padding: 18 }}>Nothing matches “{q}”.</div>}
          {results.map((item, i) => {
            const header = item.group !== lastGroup ? item.group : null
            lastGroup = item.group
            return (
              <div key={item.id}>
                {header && <div className="palette-group">{header}</div>}
                <button className={`palette-item${i === active ? ' active' : ''}`} onMouseMove={() => setActive(i)} onClick={() => run(item)}>
                  <Icon name={item.icon} size={16} />
                  <span className="grow truncate"><span className="strong">{item.title}</span>{item.subtitle && <span className="tiny subtle"> · {item.subtitle}</span>}</span>
                  {item.badge && <span className="badge">{item.badge}</span>}
                </button>
              </div>
            )
          })}
        </div>
        <div className="palette-foot tiny subtle"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>Enter</kbd> open</span><span><kbd>Ctrl</kbd>+<kbd>K</kbd> anywhere</span></div>
      </div>
    </div>
  )
}
