import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { navigate } from '../router'
import type { EndpointInfo, EndpointParam, EndpointStyle, ParamSource, ProjectDetails, ServiceInfo, SqlTestResult } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Choice, Field, MethodBadge, Modal, RouteText, Segmented, Switch, errorText } from './ui'

// ---------------------------------------------------------------- What an endpoint can do

type Source = 'template' | 'serviceCall' | 'sql' | 'entity' | 'simple'
type Behavior = { id: string; source: Source; title: string; desc: string; icon: string; method: string; path: string; needsKey?: boolean }

const ENTITY_BEHAVIORS: Behavior[] = [
  { id: 'entityList', source: 'entity', title: 'List records', desc: 'Rows from a table, page by page', icon: 'list', method: 'GET', path: '' },
  { id: 'entityGet', source: 'entity', title: 'Get one record', desc: 'A single row by its ID', icon: 'eye', method: 'GET', path: '{id}', needsKey: true },
  { id: 'entityFilter', source: 'entity', title: 'Search records', desc: 'Rows matching one field', icon: 'filter', method: 'GET', path: 'search' },
  { id: 'entityCreate', source: 'entity', title: 'Add a record', desc: 'Saves a new row', icon: 'plus', method: 'POST', path: '' },
  { id: 'entityUpdate', source: 'entity', title: 'Update a record', desc: 'Changes a row by its ID', icon: 'edit', method: 'PUT', path: '{id}', needsKey: true },
  { id: 'entityDelete', source: 'entity', title: 'Delete a record', desc: 'Removes a row by its ID', icon: 'trash', method: 'DELETE', path: '{id}', needsKey: true },
]

const SIMPLE_BEHAVIORS: Behavior[] = [
  { id: 'message', source: 'simple', title: 'Return a message', desc: 'Replies with fixed text', icon: 'message', method: 'GET', path: 'hello' },
  { id: 'echo', source: 'simple', title: 'Echo the inputs', desc: 'Replies with what it receives', icon: 'repeat', method: 'POST', path: 'echo' },
  { id: 'notImplemented', source: 'simple', title: 'Placeholder', desc: 'Reserves the path for later', icon: 'clock', method: 'GET', path: 'coming-soon' },
  { id: 'noContent', source: 'simple', title: 'Do nothing', desc: 'Accepts the call, no reply', icon: 'ban', method: 'POST', path: 'ping' },
]

const TEMPLATE: Behavior = { id: 'template', source: 'template', title: 'Copy an existing endpoint', desc: 'Same structure, checks and error handling as an endpoint you pick. Best for projects with their own pattern.', icon: 'copy', method: 'POST', path: '' }
const SERVICE: Behavior = { id: 'serviceCall', source: 'serviceCall', title: 'Call a service operation', desc: 'Runs one of the operations your services already offer.', icon: 'zap', method: 'POST', path: '' }
const SQL: Behavior = { id: 'sql', source: 'sql', title: 'Run SQL or a stored procedure', desc: 'Runs a query directly on the database, with any parameters as inputs.', icon: 'database', method: 'GET', path: '' }

const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE']
const BASE_TYPES: { value: string; label: string }[] = [
  { value: 'string', label: 'Text' }, { value: 'int', label: 'Whole number' }, { value: 'long', label: 'Big whole number' },
  { value: 'decimal', label: 'Decimal number' }, { value: 'bool', label: 'Yes / No' }, { value: 'Guid', label: 'Unique ID' },
  { value: 'DateTime', label: 'Date & time' }, { value: 'DateOnly', label: 'Date' }, { value: 'json', label: 'JSON object' },
]
const SOURCES: { value: ParamSource; label: string }[] = [
  { value: 'Query', label: 'Query string' }, { value: 'Body', label: 'Body' }, { value: 'Header', label: 'Header' },
]
const PROVIDER_LABEL: Record<string, string> = { SqlServer: 'SQL Server', PostgreSql: 'PostgreSQL', MySql: 'MySQL', Oracle: 'Oracle', Sqlite: 'SQLite' }
const IDENT = /^[A-Za-z_][A-Za-z0-9_]*$/
const ROUTE_OK = /^[A-Za-z0-9_\-./{}:?*~=]*$/

type Initial = { mode: 'add'; groupKey?: string; templateId?: string } | { mode: 'edit'; endpoint: EndpointInfo }

export function EndpointEditor({ project, initial, onClose, onSaved }: {
  project: ProjectDetails
  initial: Initial
  onClose: () => void
  onSaved: (p: ProjectDetails, message: string) => void
}) {
  const editing = initial.mode === 'edit' ? initial.endpoint : null
  const caps = project.capabilities
  const services = project.services.filter(s => s.operations.length > 0)
  const startTemplate = initial.mode === 'add' && initial.templateId ? initial.templateId : ''
  const [step, setStep] = useState<1 | 2>(editing || startTemplate ? 2 : 1)
  const [behavior, setBehavior] = useState<Behavior | null>(startTemplate ? TEMPLATE : null)

  // Group
  const preferredGroup = [...project.groups]
    .sort((a, b) => Number(b.baseRoute !== '/') - Number(a.baseRoute !== '/') || Number(b.style === project.preferredStyle) - Number(a.style === project.preferredStyle) || b.endpointCount - a.endpointCount)[0]
  const [groupKey, setGroupKey] = useState<string>(editing?.groupKey ?? (initial.mode === 'add' ? initial.groupKey : undefined) ?? preferredGroup?.key ?? '__new')
  const [newGroupName, setNewGroupName] = useState('')
  const [customGroupRoute, setCustomGroupRoute] = useState<string | null>(null)
  const [newGroupStyle, setNewGroupStyle] = useState<EndpointStyle>(caps.supportsMinimalApis ? project.preferredStyle : 'Controller')

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

  // Copy an endpoint
  const [templateId, setTemplateId] = useState<string>(startTemplate)
  const [templateOp, setTemplateOp] = useState<string>('')
  const [newOpName, setNewOpName] = useState('')
  // Service call
  const [svcName, setSvcName] = useState(services[0]?.interface ?? '')
  const [opName, setOpName] = useState('')
  // SQL
  const [sqlKind, setSqlKind] = useState<'query' | 'procedure'>('query')
  const [sqlText, setSqlText] = useState('')
  const [sqlProvider, setSqlProvider] = useState(project.dataAccess.sqlProviders[0] ?? 'SqlServer')
  const [connName, setConnName] = useState(project.dataAccess.connectionStrings[0] ?? '__new')
  const [newConnName, setNewConnName] = useState('Default')
  const [newConnValue, setNewConnValue] = useState('')
  const [sqlParams, setSqlParams] = useState<EndpointParam[]>([])
  const [procParams, setProcParams] = useState<EndpointParam[]>([])

  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const template = project.endpoints.find(e => e.id === templateId) ?? null
  const src = behavior?.source
  const isNewGroup = groupKey === '__new' && src !== 'template'
  const group = src === 'template' ? project.groups.find(g => g.key === template?.groupKey) : project.groups.find(g => g.key === groupKey)
  const newGroupRoute = customGroupRoute ?? (newGroupName.trim() ? `/api/${kebab(newGroupName)}` : '')
  const baseRoute = isNewGroup ? normalize(newGroupRoute) : group?.baseRoute ?? '/'
  const fullRoute = combine(baseRoute, route)
  const entityInfo = project.entities.find(e => e.name === entity)
  const routeParams = useMemo(() => [...route.matchAll(/\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)[^}]*\}/g)].map(m => m[1]), [route])
  const svc: ServiceInfo | undefined = services.find(s => s.interface === svcName)
  const op = svc?.operations.find(o => o.name === opName)
  const templateSvc = template?.calls ? services.find(s => s.interface === template.calls!.split('.')[0]) : undefined
  const templateOldOp = template?.calls?.split('.')[1] ?? null

  // SQL parameters follow the SQL text
  useEffect(() => {
    if (src !== 'sql' || sqlKind !== 'query') return
    const t = setTimeout(() => {
      api.sqlParameters(sqlText).then(names => setSqlParams(list => names.map(n => list.find(p => p.name === n)
        ?? { name: n, type: 'string', source: (routeParams.includes(n) ? 'Route' : 'Query') as ParamSource, optional: false }))).catch(() => {})
    }, 350)
    return () => clearTimeout(t)
  }, [sqlText, sqlKind, src, routeParams])

  // Path inputs for simple behaviors
  const inputs: EndpointParam[] = src !== 'simple' || editing ? [] : [
    ...routeParams.map(rp => ({ name: rp, type: routeTypes[rp] ?? 'string', source: 'Route' as ParamSource, optional: false })),
    ...extraInputs,
  ]
  const setInputs = (list: EndpointParam[]) => {
    setRouteTypes(Object.fromEntries(list.filter(p => p.source === 'Route').map(p => [p.name, p.type])))
    setExtraInputs(list.filter(p => p.source !== 'Route'))
  }

  const chosenOp = src === 'serviceCall' ? op?.name : src === 'template' ? (templateOp === '__new' ? newOpName : templateOp) : undefined
  const name = customName ?? suggestName(method, route, behavior, entityInfo?.name, entityInfo?.dbSetName, filterProp, chosenOp)

  const pick = (b: Behavior) => {
    setBehavior(b)
    setMethod(b.method)
    setRoute(b.path)
    if (b.source === 'entity' && entityInfo) {
      const match = project.groups.find(g => g.baseRoute.toLowerCase() === `/api/${kebab(entityInfo.dbSetName)}`)
      if (match && initial.mode === 'add' && !initial.groupKey) setGroupKey(match.key)
      if (b.id === 'entityFilter') setFilterProp(entityInfo.properties.find(p => p.type === 'string')?.name ?? entityInfo.properties[0]?.name ?? '')
    }
    setStep(2)
  }

  const chooseTemplate = (id: string) => {
    setTemplateId(id)
    setCustomName(null)
    const t = project.endpoints.find(e => e.id === id)
    if (!t) return
    setMethod(t.httpMethod === 'ANY' ? 'POST' : t.httpMethod)
    setRequiresAuth(t.requiresAuth)
    const s = t.calls ? services.find(x => x.interface === t.calls!.split('.')[0]) : undefined
    const nextOp = s ? (s.operations.find(o => !o.exposed)?.name ?? '__new') : ''
    setTemplateOp(nextOp)
    setRoute(nextOp && nextOp !== '__new' ? t.relativeRoute.replace(/[^/{}]+$/, nextOp) : t.relativeRoute.replace(/[^/{}]+$/, m => m + '2'))
  }
  const chooseTemplateOp = (value: string, newName = newOpName) => {
    setTemplateOp(value)
    setCustomName(null)
    const opForRoute = value === '__new' ? newName : value
    if (template && opForRoute) setRoute(template.relativeRoute.replace(/[^/{}]+$/, opForRoute))
  }
  const chooseOp = (value: string) => {
    setOpName(value)
    setCustomName(null)
    const o = svc?.operations.find(x => x.name === value)
    if (!o) return
    setMethod(o.parameters.some(p => !p.injected && !isSimple(p.type)) ? 'POST' : 'GET')
    setRoute(kebab(o.name.replace(/^(Get|Find|Load|Fetch)(?=[A-Z])/, '')))
  }

  // ---- Validation ----
  const problems: string[] = []
  if (!name.trim()) problems.push('Give the endpoint a name.')
  else if (!IDENT.test(name.trim())) problems.push('The name can only use letters, numbers and _ and must start with a letter.')
  if (!ROUTE_OK.test(route)) problems.push('The path contains characters that aren’t allowed.')
  if (isNewGroup && !editing && !newGroupName.trim()) problems.push('Name the new group.')
  if (behavior?.needsKey && routeParams.length !== 1) problems.push('This endpoint needs exactly one {id} placeholder in its path.')
  if (src === 'entity' && !behavior?.needsKey && routeParams.length > 0) problems.push('This kind of endpoint shouldn’t have {placeholders} in its path.')
  if (src === 'template' && !template) problems.push('Pick the endpoint to copy.')
  if (src === 'template' && templateOp === '__new' && !IDENT.test(newOpName)) problems.push('Name the new operation (letters and numbers, no spaces).')
  if (src === 'serviceCall' && !op) problems.push('Pick the operation to call.')
  if (src === 'serviceCall' && op && ['GET', 'DELETE'].includes(method) && op.parameters.some(p => !p.injected && !isSimple(p.type))) problems.push(`${op.name} receives an object, so use POST or PUT.`)
  if (src === 'sql') {
    if (!sqlText.trim()) problems.push(sqlKind === 'procedure' ? 'Enter the stored procedure name.' : 'Enter the SQL to run.')
    if (connName === '__new' && (!newConnName.trim() || !newConnValue.trim())) problems.push('Enter the new connection string’s name and value.')
  }
  if (src === 'simple' && !editing) {
    for (const p of inputs) if (!IDENT.test(p.name)) { problems.push(`“${p.name || '(empty)'}” isn’t a valid input name.`); break }
    if (new Set(inputs.map(i => i.name)).size !== inputs.length) problems.push('Two inputs have the same name.')
    if (inputs.filter(i => i.source === 'Body').length > 1) problems.push('Only one input can come from the body.')
    if ((method === 'GET' || method === 'DELETE') && inputs.some(i => i.source === 'Body')) problems.push(`${method} endpoints can’t receive a body.`)
  }
  const effectiveBase = src === 'template' ? group?.baseRoute ?? '/' : baseRoute
  const effectiveRoute = combine(effectiveBase, route)
  const conflict = project.endpoints.find(e => e.id !== editing?.id && e.httpMethod === method && shape(e.route) === shape(effectiveRoute))
  if (conflict) problems.push(`There’s already a ${method} endpoint at ${conflict.route}.`)

  const save = async () => {
    setSaving(true); setError(null)
    try {
      if (editing) {
        const r = await api.updateEndpoint(project.path, editing.id, { httpMethod: method, route, name: name.trim(), summary: summary.trim() || null, requiresAuth })
        onSaved(r.project, r.message)
        return
      }
      const r = await api.addEndpoint(project.path, {
        groupKey: src === 'template' || isNewGroup ? null : groupKey,
        newGroup: src !== 'template' && isNewGroup ? { name: newGroupName.trim(), style: newGroupStyle, baseRoute: newGroupRoute || `/api/${kebab(newGroupName)}` } : null,
        httpMethod: method,
        route,
        name: name.trim(),
        summary: summary.trim() || null,
        requiresAuth,
        behavior: behavior!.id,
        message: message.trim() || null,
        entity: src === 'entity' ? entity : null,
        filterProperty: behavior!.id === 'entityFilter' ? filterProp : null,
        parameters: src === 'simple' ? inputs : src === 'sql' ? (sqlKind === 'procedure' ? procParams : sqlParams) : null,
        templateEndpointId: src === 'template' ? templateId : null,
        serviceInterface: src === 'serviceCall' ? svcName : null,
        serviceOperation: src === 'serviceCall' ? opName : src === 'template' ? (templateOp === '__new' ? newOpName : templateOp || null) : null,
        newOperation: src === 'template' && templateOp === '__new',
        sql: src === 'sql' ? sqlText : null,
        sqlKind: src === 'sql' ? sqlKind : null,
        sqlProvider: src === 'sql' ? sqlProvider : null,
        connectionName: src === 'sql' ? (connName === '__new' ? newConnName.trim() : connName) : null,
        connectionString: src === 'sql' && connName === '__new' ? newConnValue.trim() : null,
      })
      onSaved(r.project, r.message)
    } catch (e) { setError(errorText(e)) } finally { setSaving(false) }
  }

  // ---------------------------------------------------------------- Step 1
  if (step === 1) {
    const hasEndpoints = project.endpoints.length > 0
    return (
      <Modal title="Add an endpoint" subtitle="Step 1 of 2 · How should it work?" icon="plus" onClose={onClose} size="wide"
        footer={<Button onClick={onClose}>Cancel</Button>}>
        <div className="col gap-20">
          {(hasEndpoints || services.length > 0) && (
            <section>
              <div className="section-title" style={{ fontSize: 13.5 }}><Icon name="sparkles" size={16} /> Follow how this project is built <span className="badge success" style={{ height: 20 }}>Recommended</span></div>
              <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
                {hasEndpoints && <Choice icon={TEMPLATE.icon} title={TEMPLATE.title} desc={TEMPLATE.desc} onClick={() => pick(TEMPLATE)} />}
                {services.length > 0 && <Choice icon={SERVICE.icon} title={SERVICE.title} desc={`${SERVICE.desc} ${services.length} service${services.length > 1 ? 's' : ''} found.`} onClick={() => pick(SERVICE)} />}
              </div>
            </section>
          )}
          <section>
            <div className="section-title" style={{ fontSize: 13.5 }}><Icon name="database" size={16} /> Work with the database directly</div>
            <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
              <Choice icon={SQL.icon} title={SQL.title} desc={SQL.desc + (project.dataAccess.sqlProviders.length ? ` Uses ${project.dataAccess.sqlProviders.map(p => PROVIDER_LABEL[p] ?? p).join(', ')}.` : '')} onClick={() => pick(SQL)} />
            </div>
            {project.entities.length > 0 ? (
              <>
                <div className="tiny subtle" style={{ margin: '12px 0 8px' }}>Or use one of the {project.entities.length} tables this project maps with Entity Framework:</div>
                <div className="choices">{ENTITY_BEHAVIORS.map(b => <Choice key={b.id} icon={b.icon} title={b.title} desc={b.desc} onClick={() => pick(b)} />)}</div>
              </>
            ) : (
              <div className="tiny subtle mt-8">Want a full set of table endpoints? <a href="#/new" onClick={e => { e.preventDefault(); onClose(); navigate({ page: 'new' }) }}>Create a project from a database</a>.</div>
            )}
          </section>
          <section>
            <div className="section-title" style={{ fontSize: 13.5 }}><Icon name="message" size={16} /> Simple responses</div>
            <div className="choices">{SIMPLE_BEHAVIORS.map(b => <Choice key={b.id} icon={b.icon} title={b.title} desc={b.desc} onClick={() => pick(b)} />)}</div>
          </section>
        </div>
      </Modal>
    )
  }

  // ---------------------------------------------------------------- Step 2
  return (
    <Modal
      title={editing ? 'Edit endpoint' : behavior?.title ?? 'Add an endpoint'}
      subtitle={editing ? 'Change how this endpoint is reached and described.' : 'Step 2 of 2 · Fill in the details'}
      icon={editing ? 'edit' : behavior?.icon ?? 'plus'}
      onClose={onClose}
      size="wide"
      footer={<>
        {!editing && <Button icon="arrowLeft" onClick={() => setStep(1)} style={{ marginRight: 'auto' }}>Back</Button>}
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" icon="check" loading={saving} disabled={problems.length > 0} onClick={save} title={problems[0]}>{editing ? 'Save changes' : 'Add endpoint'}</Button>
      </>}
    >
      <div className="col gap-20">
        {src === 'template' && (
          <Section icon="copy" title="Endpoint to copy" hint="The new endpoint gets the same inputs, checks, error handling and response style.">
            <EndpointPicker endpoints={project.endpoints} value={templateId} onChange={chooseTemplate} />
            {template && templateSvc && templateOldOp && (
              <div className="grid-2 mt-16">
                <Field label="Service operation it calls" hint={`The copy calls ${splitWords(templateSvc.interface.replace(/^I(?=[A-Z])/, ''))} like the original.`}>
                  <select className="select" value={templateOp} onChange={e => chooseTemplateOp(e.target.value)}>
                    <option value="__new">+ A new operation…</option>
                    {templateSvc.operations.some(o => !o.exposed) && <optgroup label="Not used by any endpoint yet">{templateSvc.operations.filter(o => !o.exposed).map(o => <option key={o.name} value={o.name}>{splitWords(o.name)}</option>)}</optgroup>}
                    <optgroup label="Already used">{templateSvc.operations.filter(o => o.exposed).map(o => <option key={o.name} value={o.name}>{splitWords(o.name)}{o.name === templateOldOp ? ' (same as the original)' : ''}</option>)}</optgroup>
                  </select>
                </Field>
                {templateOp === '__new' && (
                  <Field label="New operation name" hint="Added to the service with a placeholder a developer fills in.">
                    <input className={`input${newOpName && !IDENT.test(newOpName) ? ' invalid' : ''}`} value={newOpName} onChange={e => { const v = e.target.value.replace(/\s+/g, ''); setNewOpName(v); chooseTemplateOp('__new', v) }} placeholder="GetCustomerStatement" autoFocus />
                  </Field>
                )}
              </div>
            )}
            {template && !templateSvc && <div className="hint mt-8">This endpoint doesn’t call a service, so the copy will do exactly the same work under the new name and path.</div>}
          </Section>
        )}

        {src === 'serviceCall' && (
          <Section icon="zap" title="Operation to call">
            <div className="grid-2">
              <Field label="Service">
                <select className="select" value={svcName} onChange={e => { setSvcName(e.target.value); setOpName('') }}>
                  {services.map(s => <option key={s.interface} value={s.interface}>{splitWords(s.interface.replace(/^I(?=[A-Z])/, ''))} · {s.project}</option>)}
                </select>
              </Field>
              <Field label="Operation">
                <select className="select" value={opName} onChange={e => chooseOp(e.target.value)}>
                  <option value="">Choose…</option>
                  {svc && svc.operations.some(o => !o.exposed) && <optgroup label="Not used by any endpoint yet">{svc.operations.filter(o => !o.exposed).map(o => <option key={o.name} value={o.name}>{splitWords(o.name)}</option>)}</optgroup>}
                  {svc && <optgroup label="Already used">{svc.operations.filter(o => o.exposed).map(o => <option key={o.name} value={o.name}>{splitWords(o.name)}</option>)}</optgroup>}
                </select>
              </Field>
            </div>
            {op && (
              <div className="row wrap gap-6 mt-16">
                <span className="tiny subtle">Receives:</span>
                {op.parameters.filter(p => !p.injected).map(p => <span key={p.name} className="chip">{p.name} · {p.friendly} · {isSimple(p.type) ? 'query' : 'body'}</span>)}
                {op.parameters.filter(p => !p.injected).length === 0 && <span className="chip">nothing</span>}
                <span className="tiny subtle" style={{ marginLeft: 12 }}>Returns:</span><span className="chip">{op.returns}</span>
              </div>
            )}
          </Section>
        )}

        {src === 'sql' && (
          <SqlSection
            project={project} kind={sqlKind} onKind={setSqlKind} text={sqlText} onText={setSqlText}
            provider={sqlProvider} onProvider={setSqlProvider} connName={connName} onConnName={setConnName}
            newConnName={newConnName} onNewConnName={setNewConnName} newConnValue={newConnValue} onNewConnValue={setNewConnValue}
            params={sqlKind === 'procedure' ? procParams : sqlParams} onParams={sqlKind === 'procedure' ? setProcParams : setSqlParams} routeParams={routeParams}
          />
        )}

        {src === 'entity' && (
          <Section icon="table" title="Table">
            <div className="grid-2">
              <Field label="Table">
                <select className="select" value={entity} onChange={e => { setEntity(e.target.value); setCustomName(null) }}>
                  {project.entities.map(e => <option key={e.name} value={e.name}>{splitWords(e.dbSetName)}</option>)}
                </select>
              </Field>
              {behavior?.id === 'entityFilter' && entityInfo && (
                <Field label="Search by field">
                  <select className="select" value={filterProp} onChange={e => setFilterProp(e.target.value)}>
                    {entityInfo.properties.map(p => <option key={p.name} value={p.name}>{splitWords(p.name)}{p.type === 'string' ? ' (contains)' : ' (equals)'}</option>)}
                  </select>
                </Field>
              )}
            </div>
          </Section>
        )}

        <Section icon="send" title="Address">
          {!editing && src !== 'template' && (
            <div className="col" style={{ marginBottom: 16 }}>
              <Field label="Group" hint="Endpoints in a group share the start of their path.">
                <select className="select" value={groupKey} onChange={e => setGroupKey(e.target.value)}>
                  {[...project.groups].sort((a, b) => a.name.localeCompare(b.name)).map(g => <option key={g.key} value={g.key}>{g.name} ({g.baseRoute})</option>)}
                  <option value="__new">+ Create a new group…</option>
                </select>
              </Field>
              {isNewGroup && (
                <div className="grid-2" style={{ padding: 14, background: 'var(--bg-sunken)', borderRadius: 10 }}>
                  <Field label="Group name"><input className="input" autoFocus value={newGroupName} onChange={e => setNewGroupName(e.target.value)} placeholder="e.g. Orders" /></Field>
                  <Field label="Path starts with"><input className="input mono" value={newGroupRoute} onChange={e => setCustomGroupRoute(e.target.value)} placeholder="/api/orders" /></Field>
                  {caps.supportsMinimalApis && (
                    <Field label="Style"><Segmented<EndpointStyle> value={newGroupStyle} onChange={setNewGroupStyle} options={[{ value: 'Controller', label: 'Controller' }, { value: 'MinimalApi', label: 'Minimal API' }]} /></Field>
                  )}
                </div>
              )}
            </div>
          )}
          {src === 'template' && group && <div className="hint" style={{ marginBottom: 12 }}>It goes into the <strong>{group.name}</strong> group, right after the endpoint it copies.</div>}
          <div className="row wrap gap-6" style={{ marginBottom: 12 }}>
            {METHODS.map(m => (
              <button key={m} type="button" onClick={() => setMethod(m)} className="btn sm" style={{
                borderColor: method === m ? `var(--${m.toLowerCase()})` : undefined, background: method === m ? `var(--${m.toLowerCase()}-soft)` : undefined,
                color: method === m ? `var(--${m.toLowerCase()})` : undefined, fontFamily: 'var(--mono)', minWidth: 70,
              }}>{m}</button>
            ))}
          </div>
          <div className="input-group">
            <span className="addon" title={effectiveBase}>{effectiveBase === '/' ? '/' : effectiveBase + '/'}</span>
            <input className="input mono" value={route} onChange={e => setRoute(e.target.value.replace(/^\/+/, ''))} placeholder="(leave empty to use the group path)" />
          </div>
          <div className="hint mt-8">Use <span className="mono">{'{name}'}</span> for values inside the path, e.g. <span className="mono">{'{id}'}</span>.</div>
          <div className="preview-url mt-16"><MethodBadge method={method} /><RouteText route={effectiveRoute} /></div>
        </Section>

        <Section icon="fileText" title="Name & description">
          <div className="grid-2">
            <Field label="Name" hint="Short, unique, no spaces.">
              <input className={`input${name && !IDENT.test(name) ? ' invalid' : ''}`} value={name} onChange={e => setCustomName(e.target.value.replace(/\s+/g, ''))} placeholder="GetOrders" />
            </Field>
            <Field label="Description" hint="Shown in API docs and Postman.">
              <input className="input" value={summary} onChange={e => setSummary(e.target.value)} placeholder={src === 'template' && template?.summary ? template.summary : 'What does this endpoint do?'} />
            </Field>
          </div>
          {behavior?.id === 'message' && (
            <div className="mt-16"><Field label="Message to return"><input className="input" value={message} onChange={e => setMessage(e.target.value)} placeholder={`Hello from ${fullRoute}`} /></Field></div>
          )}
        </Section>

        {!editing && src === 'simple' && <InputsEditor inputs={inputs} onChange={setInputs} method={method} entities={project.entities.map(e => e.name)} allowHeaders={caps.supportsHeaderInputs} />}
        {editing && <Alert kind="info">To change what this endpoint does or what it receives, add a new one (you can copy this one) and delete the old one.</Alert>}

        <div className="card pad row" style={{ background: 'var(--bg-sunken)' }}>
          <div className="group-icon" style={{ background: requiresAuth ? 'var(--warning-soft)' : undefined, color: requiresAuth ? 'var(--warning)' : undefined }}><Icon name={requiresAuth ? 'lock' : 'unlock'} size={16} /></div>
          <div className="grow">
            <div className="strong">Only signed-in users</div>
            <div className="small subtle">{src === 'template' ? 'Copies follow the original’s sign-in rules; switch this to change them.' : 'Callers must send a valid access token. The project needs sign-in set up for this to work.'}</div>
          </div>
          <Switch checked={requiresAuth} onChange={setRequiresAuth} />
        </div>

        {problems.length > 0 && (route || customName || summary || templateId || opName || sqlText) && <Alert kind="warning">{problems[0]}</Alert>}
        {error && <Alert kind="error">{error}</Alert>}
      </div>
    </Modal>
  )
}

// ---------------------------------------------------------------- Pieces

function Section({ icon, title, hint, children }: { icon: string; title: string; hint?: string; children: React.ReactNode }) {
  return (
    <section>
      <div className="row gap-6" style={{ marginBottom: hint ? 2 : 10 }}><Icon name={icon} size={15} /><span className="strong">{title}</span></div>
      {hint && <div className="hint" style={{ marginBottom: 10 }}>{hint}</div>}
      {children}
    </section>
  )
}

function EndpointPicker({ endpoints, value, onChange }: { endpoints: EndpointInfo[]; value: string; onChange: (id: string) => void }) {
  const [filter, setFilter] = useState('')
  const list = endpoints.filter(e => `${e.httpMethod} ${e.route} ${e.name}`.toLowerCase().includes(filter.toLowerCase()))
  return (
    <div className="col gap-6">
      <div className="search"><Icon name="search" size={16} /><input className="input" placeholder="Search endpoints…" value={filter} onChange={e => setFilter(e.target.value)} /></div>
      <div className="folder-list" style={{ maxHeight: 220 }}>
        {list.slice(0, 200).map(e => (
          <div key={e.id} className="folder-item" onClick={() => onChange(e.id)} style={value === e.id ? { background: 'var(--accent-soft)' } : undefined}>
            <MethodBadge method={e.httpMethod} />
            <span className="grow" style={{ minWidth: 0 }}>
              <span className="route truncate" style={{ display: 'block' }}>{e.route}</span>
              {e.calls && <span className="tiny subtle">calls {splitWords(e.calls.replace(/^I(?=[A-Z])/, '').replace('.', ' · '))}</span>}
            </span>
            {value === e.id && <Icon name="check" size={16} />}
          </div>
        ))}
        {list.length === 0 && <div className="folder-item subtle" style={{ cursor: 'default' }}>No endpoints match.</div>}
      </div>
    </div>
  )
}

function SqlSection(props: {
  project: ProjectDetails; kind: 'query' | 'procedure'; onKind: (k: 'query' | 'procedure') => void; text: string; onText: (t: string) => void
  provider: string; onProvider: (p: string) => void; connName: string; onConnName: (c: string) => void
  newConnName: string; onNewConnName: (v: string) => void; newConnValue: string; onNewConnValue: (v: string) => void
  params: EndpointParam[]; onParams: (p: EndpointParam[]) => void; routeParams: string[]
}) {
  const { project, kind, text, params } = props
  const [testValues, setTestValues] = useState<Record<string, string>>({})
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState<SqlTestResult | null>(null)
  const [testError, setTestError] = useState<string | null>(null)
  const providers = ['SqlServer', 'PostgreSql', 'MySql', 'Oracle', 'Sqlite']
  const known = project.dataAccess.sqlProviders

  const test = async () => {
    setTesting(true); setTestError(null); setResult(null)
    try {
      setResult(await api.sqlTest(project.path, {
        provider: props.provider,
        connectionName: props.connName === '__new' ? null : props.connName,
        connectionString: props.connName === '__new' ? props.newConnValue : null,
        sql: text,
        values: testValues,
      }))
    } catch (e) { setTestError(errorText(e)) } finally { setTesting(false) }
  }
  const update = (i: number, patch: Partial<EndpointParam>) => props.onParams(params.map((p, j) => j === i ? { ...p, ...patch } : p))

  return (
    <Section icon="database" title="Database query">
      <div className="col gap-16">
        <div className="grid-2">
          <Field label="Database">
            <select className="select" value={props.provider} onChange={e => props.onProvider(e.target.value)}>
              {providers.map(p => <option key={p} value={p}>{PROVIDER_LABEL[p]}{known.includes(p) ? '' : ' (driver will be added)'}</option>)}
            </select>
          </Field>
          <Field label="Connection" hint="From the project’s settings (ConnectionStrings).">
            <select className="select" value={props.connName} onChange={e => props.onConnName(e.target.value)}>
              {project.dataAccess.connectionStrings.map(c => <option key={c} value={c}>{c}</option>)}
              <option value="__new">+ Add a connection string…</option>
            </select>
          </Field>
        </div>
        {props.connName === '__new' && (
          <div className="grid-2" style={{ gridTemplateColumns: '1fr 2fr', padding: 14, background: 'var(--bg-sunken)', borderRadius: 10 }}>
            <Field label="Name"><input className="input" value={props.newConnName} onChange={e => props.onNewConnName(e.target.value.replace(/\s+/g, ''))} /></Field>
            <Field label="Connection string" hint="Saved in appsettings.json."><input className="input mono" value={props.newConnValue} onChange={e => props.onNewConnValue(e.target.value)} placeholder="Server=…;Database=…;User Id=…;Password=…" /></Field>
          </div>
        )}
        <div><Segmented<'query' | 'procedure'> value={kind} onChange={props.onKind} options={[{ value: 'query', label: 'SQL query' }, { value: 'procedure', label: 'Stored procedure' }]} /></div>
        {kind === 'query' ? (
          <Field label="SQL" hint={<>Use <span className="mono">@name</span>{props.provider === 'Oracle' ? <> or <span className="mono">:name</span></> : null} for values the caller sends. They become inputs automatically.</>}>
            <textarea className="textarea code" rows={5} value={text} onChange={e => props.onText(e.target.value)} spellCheck={false}
              placeholder={props.provider === 'Oracle' ? 'SELECT * FROM CUSTOMERS WHERE CUST_NO = :customerNo' : 'SELECT * FROM Customers WHERE Id = @id'} />
          </Field>
        ) : (
          <Field label="Procedure name"><input className="input mono" value={text} onChange={e => props.onText(e.target.value.trim())} placeholder="dbo.GetCustomerBalance" /></Field>
        )}

        {(params.length > 0 || kind === 'procedure') && (
          <div className="col gap-6">
            <div className="row between">
              <span className="label">Inputs</span>
              {kind === 'procedure' && <Button size="sm" icon="plus" onClick={() => props.onParams([...params, { name: '', type: 'string', source: 'Query', optional: false }])}>Add parameter</Button>}
            </div>
            {params.map((p, i) => (
              <div key={i} className="param-row">
                <input className="input mono" value={p.name} disabled={kind === 'query'} onChange={e => update(i, { name: e.target.value.replace(/\W/g, '') })} placeholder="parameter" />
                <select className="select" value={p.type} onChange={e => update(i, { type: e.target.value })}>
                  {BASE_TYPES.filter(t => t.value !== 'json').map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
                </select>
                {props.routeParams.includes(p.name) ? <span className="badge accent" style={{ height: 38, justifyContent: 'center' }}>In the path</span> : (
                  <select className="select" value={p.source === 'Header' ? 'Header' : 'Query'} onChange={e => update(i, { source: e.target.value as ParamSource })}>
                    <option value="Query">Query string</option>
                    {project.capabilities.supportsHeaderInputs && <option value="Header">Header</option>}
                  </select>
                )}
                <label className="row gap-6 small"><input type="checkbox" className="checkbox" checked={p.optional} onChange={e => update(i, { optional: e.target.checked })} /> Optional</label>
                {kind === 'procedure' ? <Button size="sm" variant="ghost" iconOnly icon="x" onClick={() => props.onParams(params.filter((_, j) => j !== i))} /> : <span />}
              </div>
            ))}
          </div>
        )}

        {kind === 'query' && /^\s*(select|with)\b/i.test(text) && (
          <div className="card pad col gap-6" style={{ background: 'var(--bg-sunken)' }}>
            <div className="row between wrap">
              <div><span className="strong">Test the query</span> <span className="tiny subtle">Runs read-only and shows the first 20 rows.</span></div>
              <Button size="sm" icon="play" loading={testing} onClick={test} disabled={props.connName === '__new' && !props.newConnValue}>Run test</Button>
            </div>
            {params.length > 0 && (
              <div className="row wrap gap-6">
                {params.map(p => <input key={p.name} className="input" style={{ width: 170, height: 32 }} placeholder={p.name} value={testValues[p.name] ?? ''} onChange={e => setTestValues(v => ({ ...v, [p.name]: e.target.value }))} />)}
              </div>
            )}
            {testError && <Alert kind="error">{testError}</Alert>}
            {result && (
              result.columns.length === 0 ? <div className="small muted">The query returned no columns.</div> : (
                <div className="table-wrap" style={{ maxHeight: 240 }}>
                  <table className="table">
                    <thead><tr>{result.columns.map(c => <th key={c}>{c}</th>)}</tr></thead>
                    <tbody>{result.rows.map((r, i) => <tr key={i}>{r.map((v, j) => <td key={j} className="small">{v ?? <span className="subtle">null</span>}</td>)}</tr>)}</tbody>
                  </table>
                  <div className="tiny subtle" style={{ padding: 8 }}>{result.rows.length === 0 ? 'No rows found.' : `${result.rows.length}${result.more ? '+' : ''} rows`} · {result.durationMs} ms</div>
                </div>
              )
            )}
          </div>
        )}
      </div>
    </Section>
  )
}

function InputsEditor({ inputs, onChange, method, entities, allowHeaders }: { inputs: EndpointParam[]; onChange: (p: EndpointParam[]) => void; method: string; entities: string[]; allowHeaders: boolean }) {
  const types = [...BASE_TYPES, ...entities.map(e => ({ value: e, label: `${splitWords(e)} record` }))]
  const noBody = method === 'GET' || method === 'DELETE'
  const update = (i: number, patch: Partial<EndpointParam>) => onChange(inputs.map((p, j) => j === i ? { ...p, ...patch } : p))
  const add = () => {
    const hasBody = inputs.some(p => p.source === 'Body')
    onChange([...inputs, { name: '', type: noBody || hasBody ? 'string' : 'json', source: noBody || hasBody ? 'Query' : 'Body', optional: false }])
  }
  return (
    <Section icon="list" title="Inputs" hint="Values the caller sends. Path values are added automatically.">
      <div className="col gap-6">
        {inputs.length === 0 && <div className="small subtle" style={{ padding: '10px 12px', background: 'var(--bg-sunken)', borderRadius: 10 }}>No inputs. The endpoint can be called without sending any values.</div>}
        {inputs.map((p, i) => (
          <div key={i} className="param-row">
            <input className="input" value={p.name} placeholder="name" disabled={p.source === 'Route'} onChange={e => update(i, { name: e.target.value.replace(/\s+/g, '') })} />
            <select className="select" value={p.type} onChange={e => update(i, { type: e.target.value })}>
              {types.filter(t => p.source === 'Body' || (t.value !== 'json' && !entities.includes(t.value))).map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
            </select>
            {p.source === 'Route' ? <span className="badge accent" style={{ height: 38, justifyContent: 'center' }}>In the path</span> : (
              <select className="select" value={p.source} onChange={e => {
                const source = e.target.value as ParamSource
                update(i, { source, type: source !== 'Body' && (p.type === 'json' || entities.includes(p.type)) ? 'string' : p.type })
              }}>
                {SOURCES.filter(s => !(noBody && s.value === 'Body') && (allowHeaders || s.value !== 'Header')).map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
              </select>
            )}
            <label className="row gap-6 small" style={{ visibility: p.source === 'Route' ? 'hidden' : undefined }}><input type="checkbox" className="checkbox" checked={p.optional} onChange={e => update(i, { optional: e.target.checked })} /> Optional</label>
            <Button size="sm" variant="ghost" iconOnly icon="x" disabled={p.source === 'Route'} onClick={() => onChange(inputs.filter((_, j) => j !== i))} aria-label="Remove input" />
          </div>
        ))}
        <div><Button size="sm" icon="plus" onClick={add}>Add input</Button></div>
      </div>
    </Section>
  )
}

// ---------------------------------------------------------------- helpers

function isSimple(type: string) {
  return ['string', 'int', 'long', 'short', 'byte', 'bool', 'decimal', 'double', 'float', 'Guid', 'DateTime', 'DateTimeOffset', 'DateOnly', 'TimeOnly', 'TimeSpan', 'char'].includes(type.replace(/\?$/, ''))
}
function normalize(r: string) { return '/' + r.trim().replace(/^\/+|\/+$/g, '') }
function combine(a: string, b: string) {
  if (b.trim().startsWith('~/')) return '/' + b.trim().slice(2).replace(/^\/+|\/+$/g, '')
  const x = a.replace(/^\/+|\/+$/g, ''), y = b.trim().replace(/^\/+|\/+$/g, '')
  return '/' + [x, y].filter(Boolean).join('/')
}
function shape(r: string) { return r.toLowerCase().replace(/\{[^}]*\}/g, '{}').replace(/\/+$/, '') }
function kebab(s: string) { return s.trim().replace(/([a-z0-9])([A-Z])/g, '$1-$2').replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '').toLowerCase() }
function pascal(s: string) { return s.split(/[^A-Za-z0-9]+/).filter(Boolean).map(w => w[0].toUpperCase() + w.slice(1)).join('') }
function splitWords(s: string) { return s.replace(/([a-z0-9])([A-Z])/g, '$1 $2') }

function suggestName(method: string, route: string, behavior: Behavior | null, entity?: string, set?: string, filterProp?: string, operation?: string) {
  if (behavior?.source === 'entity' && entity && set) {
    switch (behavior.id) {
      case 'entityList': return `Get${set}`
      case 'entityGet': return `Get${entity}ById`
      case 'entityFilter': return `Search${set}By${filterProp || 'Field'}`
      case 'entityCreate': return `Create${entity}`
      case 'entityUpdate': return `Update${entity}`
      case 'entityDelete': return `Delete${entity}`
    }
  }
  if ((behavior?.source === 'template' || behavior?.source === 'serviceCall') && operation) return operation
  const verb = { GET: 'Get', POST: 'Create', PUT: 'Update', PATCH: 'Patch', DELETE: 'Delete' }[method] ?? 'Handle'
  const segs = route.split('/').filter(Boolean)
  const words = segs.filter(s => !s.startsWith('{')).map(pascal).join('')
  const params = segs.filter(s => s.startsWith('{')).map(s => pascal(s.replace(/[{}*?]/g, '').split(':')[0]))
  return verb + (words || 'Item') + (params.length ? 'By' + params.join('And') : '')
}
