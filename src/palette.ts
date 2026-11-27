import { useEffect } from 'react'

/** Something the command palette can do. */
export interface PaletteItem {
  id: string
  title: string
  subtitle?: string
  group: string
  icon: string
  keywords?: string
  badge?: string
  run: () => void
}

type Listener = () => void
const sources = new Map<string, PaletteItem[]>()
const listeners = new Set<Listener>()

export function paletteItems(): PaletteItem[] { return [...sources.values()].flat() }
export function onPaletteChange(l: Listener) { listeners.add(l); return () => { listeners.delete(l) } }

/** Lets a page add its own items (endpoints, tables…) while it's open. */
export function usePaletteItems(key: string, items: PaletteItem[] | null) {
  useEffect(() => {
    if (!items) return
    sources.set(key, items)
    listeners.forEach(l => l())
    return () => { sources.delete(key); listeners.forEach(l => l()) }
  }, [key, items])
}
