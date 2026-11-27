import { useEffect, useRef } from 'react'
import { EditorView, basicSetup } from 'codemirror'
import { EditorState, Compartment, type Extension } from '@codemirror/state'
import { keymap } from '@codemirror/view'
import { indentWithTab } from '@codemirror/commands'
import { StreamLanguage } from '@codemirror/language'
import { javascript } from '@codemirror/lang-javascript'
import { json } from '@codemirror/lang-json'
import { html } from '@codemirror/lang-html'
import { css } from '@codemirror/lang-css'
import { xml } from '@codemirror/lang-xml'
import { sql } from '@codemirror/lang-sql'
import { markdown } from '@codemirror/lang-markdown'
import { csharp } from '@codemirror/legacy-modes/mode/clike'
import { powerShell } from '@codemirror/legacy-modes/mode/powershell'
import { shell } from '@codemirror/legacy-modes/mode/shell'
import { yaml } from '@codemirror/legacy-modes/mode/yaml'
import { dockerFile } from '@codemirror/legacy-modes/mode/dockerfile'
import { properties } from '@codemirror/legacy-modes/mode/properties'
import { oneDark } from '@codemirror/theme-one-dark'

/** Picks highlighting from the file name. */
export function languageFor(path: string): Extension | null {
  const name = path.toLowerCase().split('/').pop() ?? ''
  const ext = name.includes('.') ? name.slice(name.lastIndexOf('.') + 1) : name
  if (name === 'dockerfile' || name.endsWith('.dockerfile')) return StreamLanguage.define(dockerFile)
  switch (ext) {
    case 'cs': case 'csx': return StreamLanguage.define(csharp)
    case 'ts': case 'mts': case 'cts': return javascript({ typescript: true })
    case 'tsx': return javascript({ typescript: true, jsx: true })
    case 'js': case 'mjs': case 'cjs': return javascript()
    case 'jsx': return javascript({ jsx: true })
    case 'json': case 'jsonc': case 'webmanifest': return json()
    case 'html': case 'htm': case 'cshtml': case 'razor': case 'vue': case 'svelte': return html()
    case 'css': case 'scss': case 'less': return css()
    case 'xml': case 'csproj': case 'vbproj': case 'fsproj': case 'props': case 'targets': case 'config': case 'resx': case 'xaml': case 'svg': case 'slnx': case 'nuspec': return xml()
    case 'sql': return sql()
    case 'md': case 'markdown': return markdown()
    case 'ps1': case 'psm1': case 'psd1': return StreamLanguage.define(powerShell)
    case 'sh': case 'bash': case 'zsh': return StreamLanguage.define(shell)
    case 'yml': case 'yaml': return StreamLanguage.define(yaml)
    case 'env': case 'ini': case 'editorconfig': case 'gitignore': case 'properties': return StreamLanguage.define(properties)
    default: return null
  }
}

const isDark = () => document.documentElement.dataset.theme === 'dark' || (document.documentElement.dataset.theme !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches)

const lightTheme = EditorView.theme({
  '&': { backgroundColor: 'var(--bg-elev)', color: 'var(--text)' },
  '.cm-gutters': { backgroundColor: 'var(--bg-sunken)', color: 'var(--text-3)', borderRight: '1px solid var(--border)' },
  '.cm-activeLine': { backgroundColor: 'color-mix(in srgb, var(--accent) 6%, transparent)' },
  '.cm-activeLineGutter': { backgroundColor: 'color-mix(in srgb, var(--accent) 10%, transparent)' },
})

export function CodeEditor({ value, path, onChange, onSave, readOnly, onCursor }: {
  value: string
  path: string
  onChange: (v: string) => void
  onSave?: () => void
  readOnly?: boolean
  onCursor?: (line: number, col: number) => void
}) {
  const host = useRef<HTMLDivElement>(null)
  const view = useRef<EditorView | null>(null)
  const cb = useRef({ onChange, onSave, onCursor })
  cb.current = { onChange, onSave, onCursor }
  const theme = useRef(new Compartment())
  const lang = useRef(new Compartment())
  const ro = useRef(new Compartment())

  useEffect(() => {
    const state = EditorState.create({
      doc: value,
      extensions: [
        basicSetup,
        keymap.of([indentWithTab, { key: 'Mod-s', preventDefault: true, run: () => { cb.current.onSave?.(); return true } }]),
        EditorView.updateListener.of(u => {
          if (u.docChanged) cb.current.onChange(u.state.doc.toString())
          if (u.selectionSet || u.docChanged) {
            const pos = u.state.selection.main.head
            const line = u.state.doc.lineAt(pos)
            cb.current.onCursor?.(line.number, pos - line.from + 1)
          }
        }),
        theme.current.of(isDark() ? oneDark : lightTheme),
        lang.current.of(languageFor(path) ?? []),
        ro.current.of(EditorState.readOnly.of(!!readOnly)),
        EditorView.theme({ '&': { height: '100%', fontSize: '13px' }, '.cm-scroller': { fontFamily: 'var(--mono)', lineHeight: '1.55' } }),
      ],
    })
    view.current = new EditorView({ state, parent: host.current! })
    const mq = matchMedia('(prefers-color-scheme: dark)')
    const retheme = () => view.current?.dispatch({ effects: theme.current.reconfigure(isDark() ? oneDark : lightTheme) })
    mq.addEventListener('change', retheme)
    const obs = new MutationObserver(retheme)
    obs.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
    return () => { mq.removeEventListener('change', retheme); obs.disconnect(); view.current?.destroy(); view.current = null }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // A different file, or the file reloaded from disk
  useEffect(() => {
    const v = view.current
    if (!v) return
    if (v.state.doc.toString() !== value) v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: value } })
  }, [value])
  useEffect(() => { view.current?.dispatch({ effects: lang.current.reconfigure(languageFor(path) ?? []) }) }, [path])
  useEffect(() => { view.current?.dispatch({ effects: ro.current.reconfigure(EditorState.readOnly.of(!!readOnly)) }) }, [readOnly])

  return <div ref={host} className="code-editor" />
}
