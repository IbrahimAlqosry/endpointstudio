import { useMemo, useState } from 'react'
import { api } from '../api'
import { navigate } from '../router'
import type { EndpointInfo, EndpointParam, EndpointStyle, ParamSource, ProjectDetails } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Choice, Field, MethodBadge, Modal, RouteText, Segmented, Switch, errorText } from './ui'

type Behavior = {
  id: string
  title: string
  desc: string
  icon: string
  method: string
  path: string
  data?: boolean
  needsKey?: boolean
}

const DATA_BEHAVIORS: Behavior[] = [
  { id: 'entityList', title: 'List records', desc: 'Returns rows from a table, page by page', icon: 'list', method: 'GET', path: '', data: true },
  { id: 'entityGet', title: 'Get one record', desc: 'Finds a single row by its ID', icon: 'eye', method: 'GET', path: '{id}', data: true, needsKey: true },
  { id: 'entityFilter', title: 'Search records', desc: 'Finds rows by the value of one field', icon: 'filter', method: 'GET', path: 'search', data: true },
  { id: 'entityCreate', title: 'Add a record', desc: 'Saves a new row to the table', icon: 'plus', method: 'POST', path: '', data: true },
  { id: 'entityUpdate', title: 'Update a record', desc: 'Changes an existing row by its ID', icon: 'edit', method: 'PUT', path: '{id}', data: true, needsKey: true },
  { id: 'entityDelete', title: 'Delete a record', desc: 'Removes a row by its ID', icon: 'trash', method: 'DELETE', path: '{id}', data: true, needsKey: true },
]

const SIMPLE_BEHAVIORS: Behavior[] = [
  { id: 'message', title: 'Return a message', desc: 'Replies with a fixed text message', icon: 'message', method: 'GET', path: 'hello' },
  { id: 'echo', title: 'Echo the inputs', desc: 'Replies with the values it receives (handy for testing)', icon: 'repeat', method: 'POST', path: 'echo' },
  { id: 'notImplemented', title: 'Placeholder', desc: 'Reserves the endpoint and replies “not implemented yet”', icon: 'clock', method: 'GET', path: 'coming-soon' },
  { id: 'noContent', title: 'Do nothing', desc: 'Accepts the call and replies with no content', icon: 'ban', method: 'POST', path: 'ping' },
]

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE']

const BASE_TYPES: { value: string; label: string }[] = [
  { value: 'string', label: 'Text' },
  { value: 'int', label: 'Whole number' },
  { value: 'long', label: 'Big whole number' },
  { value: 'decimal', label: 'Decimal number' },
  { value: 'bool', label: 'Yes / No' },
  { value: 'Guid', label: 'Unique ID' },
  { value: 'DateTime', label: 'Date & time' },
  { value: 'DateOnly', label: 'Date' },
  { value: 'json', label: 'JSON object' },
]

const SOURCES: { value: ParamSource; label: string }[] = [
  { value: 'Query', label: 'Query string' },
  { value: 'Body', label: 'Body' },
  { value: 'Header', label: 'Header' },
]

const IDENT = /^[A-Za-z_][A-Za-z0-9_]*$/
const ROUTE_OK = /^[A-Za-z0-9_\-./{}:?*~=]*$/

type Initial = { mode: 'add'; groupKey?: string } | { mode: 'edit'; endpoint: EndpointInfo }

export function EndpointEditor({ project, initial, onClose, onSaved }: {
  project: ProjectDetails
  initial: Initial
  onClose: () => void
  onSaved: (p: ProjectDetails, message: string) => void
}) {
  const editing = initial.mode === 'edit' ? initial.endpoint : null
  const hasData = project.entities.length > 0
  const [step, setStep] = useState<1 | 2>(editing ? 2 : 1)
  const [behavior, setBehavior] = useState<Behavior | null>(null)

  // Group
  const preferredGroup = [...project.groups]
    .sort((a, b) => Number(b.baseRoute !== '/') - Number(a.baseRoute !== '/') || Number(b.style === project.preferredStyle) - Number(a.style === project.preferredStyle) || b.endpointCount - a.endpointCount)[0]
  const defaultGroup = editing?.groupKey ?? (initial.mode === 'add' ? initial.groupKey : undefined) ?? preferredGroup?.key ?? '__new'
  const [groupKey, setGroupKey] = useState<string>(defaultGroup)
  const [newGroupName, setNewGroupName] = useState('')
  const [customGroupRoute, setCustomGroupRoute] = useState<string | null>(null)
  const [newGroupStyle, setNewGroupStyle] = useState<EndpointStyle>(project.preferredStyle)

  // Endpoint
  const [method, setMethod] = useState(editing?.httpMethod ?? 'GET')
  const [route, setRoute] = useState(editing?.relativeRoute ?? '')
  const [customName, setCustomName] = useState<string | null>(editing ? editing.name : null)
  const [summary, setSummary] = useState(editing?.summary ?? '')
  const [requiresAuth, setRequiresAuth] = useState(editing?.requiresAuth ?? false)
  const [message, setMessage] = useState('')
  const [entity, setEntity] = useState(project.entities[0]?.name ?? '')
  const [filterProp, setFilterProp] = useState('')
  const [extraInputs, setExtraInputs] = useState<EndpointParam[]>([])
  const [routeTypes, setRouteTypes] = useState<Record<string, string>>({})

  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const newGroupRoute = customGroupRoute ?? (newGroupName.trim() ? `/api/${kebab(newGroupName)}` : '')
  const isNewGroup = groupKey === '__new'
  const group = project.groups.find(g => g.key === groupKey)
  const baseRoute = isNewGroup ? normalize(newGroupRoute) : group?.baseRoute ?? '/'
  const fullRoute = combine(baseRoute, route)
  const entityInfo = project.entities.find(e => e.name === entity)
  const routeParams = useMemo(() => [...route.matchAll(/\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}/g)].map(m => m[1]), [route])

  // Path {placeholders} always become inputs; other inputs are edited by the user.
  const inputs: EndpointParam[] = behavior?.data || editing ? [] : [
    ...routeParams.map(rp => ({ name: rp, type: routeTypes[rp] ?? 'string', source: 'Route' as ParamSource, optional: false })),
    ...extraInputs,
  ]
  const setInputs = (list: EndpointParam[]) => {
    setRouteTypes(Object.fromEntries(list.filter(p => p.source === 'Route').map(p => [p.name, p.type])))
    setExtraInputs(list.filter(p => p.source !== 'Route'))
  }

  // Suggest a name from the method and path until the user types their own
  const name = customName ?? suggestName(method, route, behavior, entityInfo?.name, entityInfo?.dbSetName, filterProp)

  const pickBehavior = (b: Behavior) => {
    setBehavior(b)
    setMethod(b.method)
    setRoute(b.path)
    if (b.data && entityInfo) {
      const match = project.groups.find(g => g.baseRoute.toLowerCase() === `/api/${kebab(entityInfo.dbSetName)}`)
      if (match && initial.mode === 'add' && !initial.groupKey) setGroupKey(match.key)
    }
    if (b.id === 'entityFilter' && entityInfo) setFilterProp(entityInfo.properties.find(p => p.type === 'string')?.name ?? entityInfo.properties[0]?.name ?? '')
    setStep(2)
  }

  // ---- Validation ----
  const problems: string[] = []
  if (!name.trim()) problems.push('Give the endpoint a name.')
  else if (!IDENT.test(name.trim())) problems.push('The name can only use letters, numbers and _ and must start with a letter.')
  if (!ROUTE_OK.test(route)) problems.push('The path contains characters that aren’t allowed.')
  if (isNewGroup && !editing) {
    if (!newGroupName.trim()) problems.push('Name the new group.')
  }
  if (behavior?.needsKey && routeParams.length !== 1) problems.push('This endpoint needs exactly one {id} placeholder in its path.')
  if (behavior?.data && !behavior.needsKey && routeParams.length > 0) problems.push('This kind of endpoint shouldn’t have {placeholders} in its path.')
  if (behavior?.needsKey && entityInfo && !entityInfo.keyName) problems.push(`${entityInfo.name} has no single ID column, so it can’t be looked up by ID.`)
  if (!behavior?.data && !editing) {
    for (const p of inputs) if (!IDENT.test(p.name)) { problems.push(`“${p.name || '(empty)'}” isn’t a valid input name.`); break }
    if (new Set(inputs.map(i => i.name)).size !== inputs.length) problems.push('Two inputs have the same name.')
    if (inputs.filter(i => i.source === 'Body').length > 1) problems.push('Only one input can come from the body.')
    if ((method === 'GET' || method === 'DELETE') && inputs.some(i => i.source === 'Body')) problems.push(`${method} endpoints can’t receive a body.`)
  }
  const conflict = project.endpoints.find(e => e.id !== editing?.id && e.httpMethod === method && shape(e.route) === shape(fullRoute))
  if (conflict) problems.push(`There’s already a ${method} endpoint at ${conflict.route}.`)

  const save = async () => {
    setSaving(true); setError(null)
    try {
      if (editing) {
        const r = await api.updateEndpoint(project.path, editing.id, { httpMethod: method, route, name: name.trim(), summary: summary.trim() || null, requiresAuth })
        onSaved(r.project, r.message)
      } else {
        const r = await api.addEndpoint(project.path, {
          groupKey: isNewGroup ? null : groupKey,
          newGroup: isNewGroup ? { name: newGroupName.trim(), style: newGroupStyle, baseRoute: newGroupRoute || `/api/${kebab(newGroupName)}` } : null,
          httpMethod: method,
          route,
          name: name.trim(),
          summary: summary.trim() || null,
          requiresAuth,
          behavior: behavior!.id,
          message: message.trim() || null,
          entity: behavior?.data ? entity : null,
          filterProperty: behavior?.id === 'entityFilter' ? filterProp : null,
          parameters: behavior?.data ? null : inputs,
        })
        onSaved(r.project, r.message)
      }
    } catch (e) { setError(errorText(e)) } finally { setSaving(false) }
  }

  // ---------------------------------------------------------------- Step 1: behavior
  if (step === 1) {
    return (
      <Modal title="Add an endpoint" subtitle="Step 1 of 2: What should it do?" icon="plus" onClose={onClose} size="wide"
        footer={<Button onClick={onClose}>Cancel</Button>}>
        <div className="col gap-20">
          <div>
            <div className="section-title" style={{ fontSize: 13.5 }}><Icon name="database" size={16} /> Work with data</div>
            {hasData ? (
              <div className="choices">
                {DATA_BEHAVIORS.map(b => <Choice key={b.id} icon={b.icon} title={b.title} desc={b.desc} onClick={() => pickBehavior(b)} />)}
              </div>
            ) : (
              <Alert kind="info">
                This project isn’t connected to a database yet. To get ready-made data endpoints, <a href="#/new" onClick={e => { e.preventDefault(); onClose(); navigate({ page: 'new' }) }} className="strong">create a project from a database</a>.
              </Alert>
            )}
          </div>
          <div>
            <div className="section-title" style={{ fontSize: 13.5 }}><Icon name="zap" size={16} /> Simple responses</div>
            <div className="choices">
              {SIMPLE_BEHAVIORS.map(b => <Choice key={b.id} icon={b.icon} title={b.title} desc={b.desc} onClick={() => pickBehavior(b)} />)}
            </div>
          </div>
        </div>
      </Modal>
    )
  }

  // ---------------------------------------------------------------- Step 2: details
  return (
    <Modal
      title={editing ? 'Edit endpoint' : behavior?.title ?? 'Add an endpoint'}
      subtitle={editing ? 'Change how this endpoint is reached and described.' : 'Step 2 of 2: Fill in the details.'}
      icon={editing ? 'edit' : behavior?.icon ?? 'plus'}
      onClose={onClose}
      size="wide"
      footer={<>
        {!editing && <Button icon="arrowLeft" onClick={() => setStep(1)} style={{ marginRight: 'auto' }}>Back</Button>}
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" icon="check" loading={saving} disabled={problems.length > 0} onClick={save} title={problems[0]}>
          {editing ? 'Save changes' : 'Add endpoint'}
        </Button>
      </>}
    >
      <div className="col gap-20">
        {/* Data source */}
        {behavior?.data && (
          <div className="grid-2">
            <Field label="Table">
              <select className="select" value={entity} onChange={e => { setEntity(e.target.value); setCustomName(null) }}>
                {project.entities.map(e => <option key={e.name} value={e.name}>{splitWords(e.dbSetName)}</option>)}
              </select>
            </Field>
            {behavior.id === 'entityFilter' && entityInfo && (
              <Field label="Search by field">
                <select className="select" value={filterProp} onChange={e => setFilterProp(e.target.value)}>
                  {entityInfo.properties.map(p => <option key={p.name} value={p.name}>{splitWords(p.name)}{p.type === 'string' ? ' (contains)' : ' (equals)'}</option>)}
                </select>
              </Field>
            )}
          </div>
        )}

        {/* Group */}
        {!editing && (
          <div className="col">
            <Field label="Group" hint="Groups keep related endpoints together and share the start of their path.">
              <select className="select" value={groupKey} onChange={e => setGroupKey(e.target.value)}>
                {[...project.groups].sort((a, b) => a.name.localeCompare(b.name)).map(g => <option key={g.key} value={g.key}>{g.name} ({g.baseRoute})</option>)}
                <option value="__new">+ Create a new group…</option>
              </select>
            </Field>
            {isNewGroup && (
              <div className="card pad col" style={{ background: 'var(--bg-sunken)' }}>
                <div className="grid-2">
                  <Field label="Group name"><input className="input" autoFocus value={newGroupName} onChange={e => setNewGroupName(e.target.value)} placeholder="e.g. Orders" /></Field>
                  <Field label="Path starts with"><input className="input mono" value={newGroupRoute} onChange={e => setCustomGroupRoute(e.target.value)} placeholder="/api/orders" /></Field>
                </div>
                <Field label="Style">
                  <Segmented<EndpointStyle> value={newGroupStyle} onChange={setNewGroupStyle} options={[{ value: 'Controller', label: 'Controller' }, { value: 'MinimalApi', label: 'Minimal API' }]} />
                </Field>
              </div>
            )}
          </div>
        )}

        {/* Method + path */}
        <Field label="Method">
          <div className="row wrap gap-6">
            {METHODS.map(m => (
              <button key={m} type="button" onClick={() => setMethod(m)} className="btn sm" style={{
                borderColor: method === m ? `var(--${m.toLowerCase()})` : undefined,
                background: method === m ? `var(--${m.toLowerCase()}-soft)` : undefined,
                color: method === m ? `var(--${m.toLowerCase()})` : undefined,
                fontFamily: 'var(--mono)', minWidth: 70,
              }}>{m}</button>
            ))}
          </div>
        </Field>

        <Field label="Path" hint={<>Use <span className="mono">{'{name}'}</span> for values that are part of the path, e.g. <span className="mono">{'{id}'}</span>.</>}>
          <div className="input-group">
            <span className="addon" title={baseRoute}>{baseRoute === '/' ? '/' : baseRoute + '/'}</span>
            <input className="input mono" value={route} onChange={e => setRoute(e.target.value.replace(/^\/+/, ''))} placeholder="(leave empty to use the group path)" />
          </div>
        </Field>

        <div className="preview-url">
          <MethodBadge method={method} />
          <RouteText route={fullRoute} />
        </div>

        <div className="grid-2">
          <Field label="Name" hint="A short unique name, written without spaces.">
            <input className={`input${name && !IDENT.test(name) ? ' invalid' : ''}`} value={name} onChange={e => setCustomName(e.target.value.replace(/\s+/g, ''))} placeholder="GetOrders" />
          </Field>
          <Field label="Description" hint="Shown in API docs and in Postman.">
            <input className="input" value={summary} onChange={e => setSummary(e.target.value)} placeholder="What does this endpoint do?" />
          </Field>
        </div>

        {behavior?.id === 'message' && (
          <Field label="Message to return">
            <input className="input" value={message} onChange={e => setMessage(e.target.value)} placeholder={`Hello from ${fullRoute}`} />
          </Field>
        )}

        {/* Inputs (simple behaviors) */}
        {!editing && behavior && !behavior.data && (
          <InputsEditor inputs={inputs} onChange={setInputs} method={method} entities={project.entities.map(e => e.name)} />
        )}

        {editing && (
          <Alert kind="info">To change what this endpoint does or what it receives, delete it and add a new one.</Alert>
        )}

        <div className="card pad row" style={{ background: 'var(--bg-sunken)' }}>
          <div className="group-icon" style={{ background: requiresAuth ? 'var(--warning-soft)' : undefined, color: requiresAuth ? 'var(--warning)' : undefined }}>
            <Icon name={requiresAuth ? 'lock' : 'unlock'} size={16} />
          </div>
          <div className="grow">
            <div className="strong">Only signed-in users</div>
            <div className="small subtle">Callers must send a valid access token. Your project needs sign-in set up for this to work.</div>
          </div>
          <Switch checked={requiresAuth} onChange={setRequiresAuth} />
        </div>

        {problems.length > 0 && (name || route || summary) && <Alert kind="warning">{problems[0]}</Alert>}
        {error && <Alert kind="error">{error}</Alert>}
      </div>
    </Modal>
  )
}

function InputsEditor({ inputs, onChange, method, entities }: { inputs: EndpointParam[]; onChange: (p: EndpointParam[]) => void; method: string; entities: string[] }) {
  const types = [...BASE_TYPES, ...entities.map(e => ({ value: e, label: `${splitWords(e)} record` }))]
  const noBody = method === 'GET' || method === 'DELETE'
  const update = (i: number, patch: Partial<EndpointParam>) => onChange(inputs.map((p, j) => j === i ? { ...p, ...patch } : p))
  const add = () => {
    const hasBody = inputs.some(p => p.source === 'Body')
    onChange([...inputs, { name: '', type: noBody || hasBody ? 'string' : 'json', source: noBody || hasBody ? 'Query' : 'Body', optional: false }])
  }
  return (
    <div className="col">
      <div className="row between">
        <div>
          <div className="label">Inputs</div>
          <div className="hint">Values the caller sends. Path values are added automatically from the path.</div>
        </div>
        <Button size="sm" icon="plus" onClick={add}>Add input</Button>
      </div>
      {inputs.length === 0 ? (
        <div className="small subtle" style={{ padding: '10px 12px', background: 'var(--bg-sunken)', borderRadius: 10 }}>No inputs. This endpoint can be called without sending any values.</div>
      ) : (
        <div className="col gap-6">
          {inputs.map((p, i) => (
            <div key={i} className="param-row">
              <input className="input" value={p.name} placeholder="name" disabled={p.source === 'Route'} onChange={e => update(i, { name: e.target.value.replace(/\s+/g, '') })} />
              <select className="select" value={p.type} onChange={e => update(i, { type: e.target.value })}>
                {types.filter(t => p.source === 'Body' || (t.value !== 'json' && !entities.includes(t.value))).map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
              </select>
              {p.source === 'Route' ? (
                <span className="badge accent" style={{ height: 38, justifyContent: 'center' }}>In the path</span>
              ) : (
                <select className="select" value={p.source} onChange={e => {
                  const source = e.target.value as ParamSource
                  update(i, { source, type: source !== 'Body' && (p.type === 'json' || entities.includes(p.type)) ? 'string' : p.type })
                }}>
                  {SOURCES.filter(s => !(noBody && s.value === 'Body')).map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
                </select>
              )}
              <label className="row gap-6 small" title="Optional" style={{ visibility: p.source === 'Route' ? 'hidden' : undefined }}>
                <input type="checkbox" className="checkbox" checked={p.optional} onChange={e => update(i, { optional: e.target.checked })} /> Optional
              </label>
              <Button size="sm" variant="ghost" iconOnly icon="x" disabled={p.source === 'Route'} onClick={() => onChange(inputs.filter((_, j) => j !== i))} aria-label="Remove input" />
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- helpers

function normalize(r: string) { return '/' + r.trim().replace(/^\/+|\/+$/g, '') }
function combine(a: string, b: string) {
  const x = a.replace(/^\/+|\/+$/g, ''), y = b.trim().replace(/^\/+|\/+$/g, '')
  return '/' + [x, y].filter(Boolean).join('/')
}
function shape(r: string) { return r.toLowerCase().replace(/\{[^}]*\}/g, '{}').replace(/\/+$/, '') }
function kebab(s: string) { return s.trim().replace(/([a-z0-9])([A-Z])/g, '$1-$2').replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '').toLowerCase() }
function pascal(s: string) { return s.split(/[^A-Za-z0-9]+/).filter(Boolean).map(w => w[0].toUpperCase() + w.slice(1)).join('') }
function splitWords(s: string) { return s.replace(/([a-z0-9])([A-Z])/g, '$1 $2') }

function suggestName(method: string, route: string, behavior: Behavior | null, entity?: string, set?: string, filterProp?: string) {
  if (behavior?.data && entity && set) {
    switch (behavior.id) {
      case 'entityList': return `Get${set}`
      case 'entityGet': return `Get${entity}ById`
      case 'entityFilter': return `Search${set}By${filterProp || 'Field'}`
      case 'entityCreate': return `Create${entity}`
      case 'entityUpdate': return `Update${entity}`
      case 'entityDelete': return `Delete${entity}`
    }
  }
  const verb = { GET: 'Get', POST: 'Create', PUT: 'Update', PATCH: 'Patch', DELETE: 'Delete' }[method] ?? 'Handle'
  const segs = route.split('/').filter(Boolean)
  const words = segs.filter(s => !s.startsWith('{')).map(pascal).join('')
  const params = segs.filter(s => s.startsWith('{')).map(s => pascal(s.replace(/[{}*?]/g, '').split(':')[0]))
  return verb + (words || 'Item') + (params.length ? 'By' + params.join('And') : '')
}
