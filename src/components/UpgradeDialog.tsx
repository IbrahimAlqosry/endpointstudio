import { useEffect, useState } from 'react'
import { api } from '../api'
import type { BuildResult, ProjectDetails, TargetOption, UpgradePlan } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Modal, Spinner, Switch, errorText } from './ui'

export function UpgradeDialog({ project, onClose, onDone }: {
  project: ProjectDetails
  onClose: () => void
  onDone: (p: ProjectDetails, message: string) => void
}) {
  const [targets, setTargets] = useState<TargetOption[]>([])
  const [major, setMajor] = useState<number | null>(null)
  const [withRefs, setWithRefs] = useState(true)
  const [plan, setPlan] = useState<UpgradePlan | null>(null)
  const [loading, setLoading] = useState(false)
  const [phase, setPhase] = useState<'plan' | 'applying' | 'building' | 'done'>('plan')
  const [build, setBuild] = useState<BuildResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const current = currentMajor(project.targetFramework)

  useEffect(() => {
    api.system().then(s => {
      const options = s.targets.filter(t => t.major > current)
      setTargets(options)
      setMajor((options.find(t => t.recommended) ?? options[0])?.major ?? null)
    }).catch(e => setError(errorText(e)))
  }, [current])

  useEffect(() => {
    if (major == null) return
    setLoading(true); setError(null)
    api.upgradePlan(project.path, { targetMajor: major, includeReferences: withRefs })
      .then(setPlan).catch(e => setError(errorText(e))).finally(() => setLoading(false))
  }, [major, withRefs, project.path])

  const apply = async () => {
    if (major == null) return
    setPhase('applying'); setError(null)
    try {
      const r = await api.upgrade(project.path, { targetMajor: major, includeReferences: withRefs })
      setPhase('building')
      try { setBuild(await api.build(project.path)) } catch { /* build check is optional */ }
      setPhase('done')
      onDone(r.project, r.message)
    } catch (e) { setError(errorText(e)); setPhase('plan') }
  }

  if (project.capabilities.platform === 'NetFramework') {
    return (
      <Modal title="Upgrade .NET version" icon="rocket" onClose={onClose} footer={<Button onClick={onClose}>Close</Button>}>
        <Alert kind="info">.NET Framework projects can't be upgraded automatically: moving to modern .NET means rewriting the web layer (System.Web → ASP.NET Core). Create a new project with the database wizard and move the logic over, or use Microsoft's .NET Upgrade Assistant in Visual Studio.</Alert>
      </Modal>
    )
  }

  return (
    <Modal
      title="Upgrade .NET version"
      subtitle={`Move ${project.name} from ${project.capabilities.frameworkLabel} to a newer .NET. Every change can be undone.`}
      icon="rocket"
      onClose={onClose}
      size="wide"
      footer={phase === 'done' ? <Button variant="primary" onClick={onClose}>Done</Button> : <>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" icon="rocket" loading={phase !== 'plan'} disabled={!plan?.canApply || loading} onClick={apply}>
          {phase === 'applying' ? 'Upgrading…' : phase === 'building' ? 'Checking the build…' : `Upgrade to .NET ${major ?? ''}`}
        </Button>
      </>}
    >
      {phase === 'done' ? (
        <div className="col gap-16">
          <div className="empty" style={{ padding: '10px 0' }}>
            <div className="success-burst" style={build && !build.success ? { background: 'var(--warning-soft)', color: 'var(--warning)' } : undefined}>
              <Icon name={build && !build.success ? 'alert' : 'check'} size={34} stroke={2.5} />
            </div>
            <h3>{build?.success === false ? 'Upgraded, but the build has problems' : `Now on .NET ${major}`}</h3>
            <p className="muted small" style={{ maxWidth: 520 }}>
              {build?.success === false
                ? 'Some code needs small changes for the new version. The problems are listed below; you can also undo the upgrade from History.'
                : 'The project builds with the new version. Run it and try a few endpoints to be sure everything behaves the same.'}
            </p>
          </div>
          {build && !build.success && (
            <div className="table-wrap" style={{ maxHeight: 260 }}>
              <table className="table">
                <thead><tr><th>Where</th><th>Problem</th></tr></thead>
                <tbody>{build.errors.map((e, i) => <tr key={i}><td className="small">{e.file?.split('/').pop() ?? 'Project'}{e.line ? ` · line ${e.line}` : ''}</td><td className="small">{e.message}</td></tr>)}</tbody>
              </table>
            </div>
          )}
        </div>
      ) : (
        <div className="col gap-20">
          {targets.length === 0 && !error ? (
            <Alert kind="info">This project already targets the newest .NET version installed on this computer.</Alert>
          ) : (
            <div className="choices" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' }}>
              {targets.map(t => (
                <button key={t.tfm} type="button" className={`choice${major === t.major ? ' selected' : ''}`} onClick={() => setMajor(t.major)} style={{ flexDirection: 'column', gap: 6 }}>
                  <span className="row between" style={{ width: '100%' }}>
                    <span className="choice-title" style={{ fontSize: 15 }}>{t.label}</span>
                    {t.recommended && <span className="badge success" style={{ height: 20 }}>Recommended</span>}
                  </span>
                  <span className="choice-desc">{t.support}</span>
                </button>
              ))}
            </div>
          )}

          {project.referencedProjects.length > 0 && (
            <Switch checked={withRefs} onChange={setWithRefs} label={`Also upgrade the ${project.referencedProjects.length} project${project.referencedProjects.length === 1 ? '' : 's'} it uses (${project.referencedProjects.map(r => r.name).join(', ')})`} />
          )}

          {error && <Alert kind="error">{error}</Alert>}
          {loading && <div className="row small subtle"><Spinner /> Working out what needs to change…</div>}

          {plan && !loading && (
            <>
              {plan.warnings.map(w => <Alert key={w} kind="warning">{w}</Alert>)}
              {plan.steps.length > 0 && (
                <div>
                  <div className="label" style={{ marginBottom: 8 }}>What will change ({plan.steps.length})</div>
                  <div className="table-wrap" style={{ maxHeight: 300 }}>
                    <table className="table">
                      <thead><tr><th>Project</th><th>Change</th></tr></thead>
                      <tbody>
                        {plan.steps.map((s, i) => (
                          <tr key={i} title={s.detail}>
                            <td className="small muted" style={{ whiteSpace: 'nowrap' }}>{s.project}</td>
                            <td className="small">
                              <span className={`badge ${s.kind === 'framework' ? 'accent' : s.kind === 'remove' ? 'warning' : ''}`} style={{ marginRight: 8 }}>{s.kind === 'framework' ? 'Version' : s.kind === 'remove' ? 'Remove' : 'Package'}</span>
                              {s.title}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <div className="tiny subtle mt-8">Packages that other packages need at newer versions are adjusted automatically, and the build is checked afterwards.</div>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </Modal>
  )
}

function currentMajor(tfm: string) {
  const m = tfm.match(/^net(?:coreapp)?(\d+)\./)
  return m ? Number(m[1]) : 0
}
