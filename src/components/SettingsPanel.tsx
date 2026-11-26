import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { SettingChange, SettingEntry, SettingsInfo } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Empty, Field, Modal, Spinner, Switch, errorText, useToast } from './ui'

type Draft = Record<string, { value: string | null; kind: string; remove?: boolean; added?: boolean }>

/** View and edit appsettings*.json / Web.config as a friendly list, with environments and secret masking. */
export function SettingsPanel({ path, onChanged }: { path: string; onChanged: () => void }) {
  const toast = useToast()
  const [info, setInfo] = useState<SettingsInfo | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [fileName, setFileName] = useState<string>('')
  const [draft, setDraft] = useState<Draft>({})
  const [reveal, setReveal] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')
  const [saving, setSaving] = useState(false)
  const [adding, setAdding] = useState(false)
  const [envOpen, setEnvOpen] = useState(false)

  const load = () => api.settings(path).then(s => { setInfo(s); setFileName(f => f || s.files[0]?.name || '') }).catch(e => setError(errorText(e)))
  useEffect(() => { load() }, [path]) // eslint-disable-line react-hooks/exhaustive-deps

  const file = info?.files.find(f => f.name === fileName)
  const entries = useMemo(() => {
    const base = file?.entries ?? []
    const added = Object.entries(draft).filter(([, d]) => d.added).map(([key, d]) => ({ key, value: d.value, kind: d.kind, secret: false } as SettingEntry))
    return [...base, ...added].filter(e => e.key.toLowerCase().includes(filter.toLowerCase()) || (e.value ?? '').toLowerCase().includes(filter.toLowerCase()))
  }, [file, draft, filter])
  const sections = useMemo(() => {
    const map = new Map<string, SettingEntry[]>()
    for (const e of entries) {
      const section = e.key.includes(':') ? e.key.split(':')[0] : 'General'
      map.set(section, [...(map.get(section) ?? []), e])
    }
    return [...map.entries()]
  }, [entries])
  const changes = Object.keys(draft).length

  const switchFile = (name: string) => {
    if (changes > 0 && !confirm('You have unsaved changes. Discard them?')) return
    setDraft({}); setFileName(name)
  }

  const save = async () => {
    if (!file) return
    setSaving(true)
    try {
      const list: SettingChange[] = Object.entries(draft).map(([key, d]) => ({ key, value: d.value, kind: d.kind, remove: !!d.remove }))
      const r = await api.saveSettings(path, file.name, list)
      setInfo(r.settings); setDraft({}); onChanged()
      toast.success(r.message)
    } catch (e) { toast.error(errorText(e)) } finally { setSaving(false) }
  }

  if (error) return <Alert kind="error">{error}</Alert>
  if (!info) return <div className="empty"><Spinner large /></div>
  if (info.files.length === 0) return <div className="card"><Empty icon="fileText" title="No settings files">This project has no appsettings.json or Web.config.</Empty></div>

  return (
    <div className="col gap-16">
      <div className="row wrap between">
        <div className="segmented">
          {info.files.map(f => (
            <button key={f.name} className={f.name === fileName ? 'active' : ''} onClick={() => switchFile(f.name)}>
              {f.environment ? <><span className="subtle">Environment</span> {f.environment}</> : f.format === 'xml' ? 'Web.config' : 'Main settings'}
            </button>
          ))}
        </div>
        <div className="row gap-6">
          {file?.format === 'json' && <Button size="sm" icon="plus" onClick={() => setEnvOpen(true)}>Add environment</Button>}
          <Button size="sm" icon="plus" onClick={() => setAdding(true)}>Add setting</Button>
        </div>
      </div>

      {file?.environment && <Alert kind="info">Values here replace the main settings when the app runs in the <strong>{file.environment}</strong> environment.</Alert>}
      {file?.hasComments && <Alert kind="warning">This file contains comments. Saving changes rewrites the file without them.</Alert>}

      <div className="search"><Icon name="search" size={16} /><input className="input" placeholder="Search settings…" value={filter} onChange={e => setFilter(e.target.value)} /></div>

      {sections.length === 0 && <div className="card"><Empty icon="fileText" title="No settings yet">Add the first setting to this file.</Empty></div>}
      {sections.map(([section, list]) => (
        <div key={section} className="card">
          <div className="group-header" style={{ cursor: 'default' }}>
            <div className="group-icon"><Icon name={/connection/i.test(section) ? 'database' : /log/i.test(section) ? 'fileText' : /jwt|token|auth/i.test(section) ? 'key' : 'layers'} size={15} /></div>
            <div className="group-title">{splitWords(section)}</div>
            <span className="tiny subtle">{list.length} setting{list.length === 1 ? '' : 's'}</span>
          </div>
          {list.map(e => {
            const d = draft[e.key]
            const value = d ? d.value : e.value
            const removed = d?.remove
            const hidden = e.secret && !reveal.has(e.key)
            const label = e.key.includes(':') ? e.key.split(':').slice(1).join(' › ') : e.key
            return (
              <div key={e.key} className="endpoint" style={{ gridTemplateColumns: 'minmax(160px, 1fr) minmax(0, 2fr) auto', cursor: 'default', opacity: removed ? .45 : 1 }}>
                <div style={{ minWidth: 0 }}>
                  <div className="strong small truncate" title={e.key} style={{ textDecoration: removed ? 'line-through' : undefined }}>{splitWords(label)}</div>
                  <div className="tiny subtle truncate mono">{e.key}</div>
                </div>
                <div>
                  {e.kind === 'bool' ? (
                    <Switch checked={value === 'true'} disabled={removed} onChange={v => setDraft(x => ({ ...x, [e.key]: { ...x[e.key], value: String(v), kind: 'bool' } }))} />
                  ) : e.kind === 'section' || e.kind === 'error' || e.kind === 'array' ? (
                    <span className="small subtle">{e.kind === 'section' ? 'Empty section' : e.kind === 'array' ? 'Empty list' : value}</span>
                  ) : (
                    <div className="row gap-4">
                      <input className={`input${e.secret ? ' mono' : ''}`} style={{ height: 34 }} disabled={removed}
                        type={hidden ? 'password' : 'text'} inputMode={e.kind === 'number' ? 'decimal' : undefined}
                        value={value ?? ''} placeholder={e.kind === 'null' ? '(empty)' : ''}
                        onChange={ev => setDraft(x => ({ ...x, [e.key]: { ...x[e.key], value: ev.target.value, kind: e.kind === 'null' ? 'string' : e.kind } }))} />
                      {e.secret && <Button size="sm" variant="ghost" iconOnly icon="eye" title={hidden ? 'Show' : 'Hide'} onClick={() => setReveal(r => { const n = new Set(r); if (n.has(e.key)) n.delete(e.key); else n.add(e.key); return n })} />}
                    </div>
                  )}
                </div>
                <div className="row gap-4">
                  {e.secret && <span className="badge warning" title="Looks like a password, key or connection string"><Icon name="lock" size={11} />Secret</span>}
                  {d && !d.added && <Button size="sm" variant="ghost" iconOnly icon="undo" title="Undo this change" onClick={() => setDraft(x => { const n = { ...x }; delete n[e.key]; return n })} />}
                  {!removed && <Button size="sm" variant="ghost" iconOnly icon="trash" title="Remove" onClick={() => setDraft(x => d?.added ? (() => { const n = { ...x }; delete n[e.key]; return n })() : ({ ...x, [e.key]: { value: null, kind: e.kind, remove: true } }))} />}
                </div>
              </div>
            )
          })}
        </div>
      ))}

      {info.launchProfiles.length > 0 && (
        <div className="card pad">
          <div className="row gap-6" style={{ marginBottom: 8 }}><Icon name="play" size={15} /><span className="strong">How it runs on a developer's computer</span></div>
          <div className="col gap-4">
            {info.launchProfiles.map(p => <div key={p.name} className="row small"><span className="strong" style={{ minWidth: 120 }}>{p.name}</span><span className="mono subtle grow">{p.urls ?? '—'}</span>{p.environment && <span className="badge">{p.environment}</span>}</div>)}
          </div>
        </div>
      )}

      {changes > 0 && (
        <div className="card pad row" style={{ position: 'sticky', bottom: 12, boxShadow: 'var(--shadow-lg)', borderColor: 'var(--accent)' }}>
          <Icon name="info" size={18} />
          <span className="grow strong">{changes} unsaved change{changes === 1 ? '' : 's'} in {fileName}</span>
          <Button onClick={() => setDraft({})}>Discard</Button>
          <Button variant="primary" icon="check" loading={saving} onClick={save}>Save changes</Button>
        </div>
      )}

      {adding && file && <AddSettingDialog web={file.format === 'xml'} onClose={() => setAdding(false)} onAdd={(key, value, kind) => { setDraft(x => ({ ...x, [key]: { value, kind, added: true } })); setAdding(false) }} />}
      {envOpen && <AddEnvironmentDialog onClose={() => setEnvOpen(false)} onAdd={async name => {
        try { const r = await api.addEnvironment(path, name); setInfo(r.settings); setFileName(`appsettings.${name}.json`); setDraft({}); setEnvOpen(false); onChanged(); toast.success(r.message) }
        catch (e) { toast.error(errorText(e)) }
      }} />}
    </div>
  )
}

function AddSettingDialog({ web, onClose, onAdd }: { web: boolean; onClose: () => void; onAdd: (key: string, value: string, kind: string) => void }) {
  const [section, setSection] = useState(web ? 'appSettings' : '')
  const [name, setName] = useState('')
  const [kind, setKind] = useState('string')
  const [value, setValue] = useState('')
  const key = [section.trim(), name.trim()].filter(Boolean).join(':')
  const valid = /^[A-Za-z0-9_.\-]+$/.test(name.trim()) && (!section || /^[A-Za-z0-9_.:\-]+$/.test(section.trim()))
  return (
    <Modal title="Add a setting" icon="plus" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!valid} onClick={() => onAdd(key, kind === 'bool' ? (value === 'true' ? 'true' : 'false') : value, kind)}>Add</Button></>}>
      <div className="col gap-16">
        {web ? (
          <Field label="Kind">
            <select className="select" value={section} onChange={e => setSection(e.target.value)}>
              <option value="appSettings">App setting</option>
              <option value="connectionStrings">Connection string</option>
            </select>
          </Field>
        ) : (
          <Field label="Section" hint="Optional, e.g. ConnectionStrings or Smtp. Use : for deeper levels.">
            <input className="input" value={section} onChange={e => setSection(e.target.value.replace(/\s+/g, ''))} placeholder="ConnectionStrings" />
          </Field>
        )}
        <Field label="Name"><input className="input" autoFocus value={name} onChange={e => setName(e.target.value.replace(/\s+/g, ''))} placeholder="Default" /></Field>
        {!web && (
          <Field label="Type">
            <select className="select" value={kind} onChange={e => setKind(e.target.value)}>
              <option value="string">Text</option><option value="number">Number</option><option value="bool">Yes / No</option>
            </select>
          </Field>
        )}
        <Field label="Value">
          {kind === 'bool' ? <Switch checked={value === 'true'} onChange={v => setValue(String(v))} label={value === 'true' ? 'Yes' : 'No'} /> :
            <input className="input" value={value} onChange={e => setValue(e.target.value)} inputMode={kind === 'number' ? 'decimal' : undefined} />}
        </Field>
      </div>
    </Modal>
  )
}

function AddEnvironmentDialog({ onClose, onAdd }: { onClose: () => void; onAdd: (name: string) => void }) {
  const [name, setName] = useState('Staging')
  return (
    <Modal title="Add an environment" subtitle="Creates appsettings.{Name}.json for values that differ in that environment." icon="layers" size="narrow" onClose={onClose}
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!/^[A-Za-z][A-Za-z0-9]*$/.test(name)} onClick={() => onAdd(name)}>Add</Button></>}>
      <Field label="Name" hint="Common names: Development, Staging, Production.">
        <input className="input" autoFocus value={name} onChange={e => setName(e.target.value.replace(/[^A-Za-z0-9]/g, ''))} />
      </Field>
    </Modal>
  )
}

function splitWords(s: string) { return s.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/_/g, ' ') }
