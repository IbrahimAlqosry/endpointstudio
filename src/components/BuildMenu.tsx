import { useState } from 'react'
import { api } from '../api'
import { Icon } from './Icon'
import { JobView } from './JobView'
import { Button, Modal, errorText, useToast } from './ui'

/** Build, rebuild or clean a project or a whole solution, in Debug or Release. */
export function BuildMenu({ path, label = 'Build', size, variant }: { path: string; label?: string; size?: 'sm'; variant?: 'primary' | 'default' | 'ghost' }) {
  const toast = useToast()
  const [open, setOpen] = useState(false)
  const [config, setConfig] = useState<'Debug' | 'Release'>('Debug')
  const [job, setJob] = useState<{ id: string; title: string } | null>(null)
  const go = async (action: 'build' | 'rebuild' | 'clean') => {
    setOpen(false)
    try { const r = await api.buildTarget(path, action, config); setJob({ id: r.jobId, title: `${action[0].toUpperCase() + action.slice(1)} ${path.split(/[\\/]/).pop()} (${config})` }) }
    catch (e) { toast.error(errorText(e)) }
  }
  return (
    <div style={{ position: 'relative' }}>
      <Button size={size} variant={variant} icon="hammer" onClick={() => setOpen(o => !o)}>{label} <Icon name="chevronDown" size={12} /></Button>
      {open && (
        <>
          <div style={{ position: 'fixed', inset: 0, zIndex: 20 }} onClick={() => setOpen(false)} />
          <div className="menu" style={{ right: 0, width: 230 }}>
            <div className="segmented" style={{ margin: 6 }}>
              {(['Debug', 'Release'] as const).map(c => <button key={c} className={config === c ? 'active' : ''} onClick={() => setConfig(c)}>{c}</button>)}
            </div>
            <button className="menu-item" onClick={() => go('build')}><div className="strong">Build</div><div className="tiny subtle">Compile what changed</div></button>
            <button className="menu-item" onClick={() => go('rebuild')}><div className="strong">Rebuild</div><div className="tiny subtle">Compile everything from scratch</div></button>
            <button className="menu-item" onClick={() => go('clean')}><div className="strong">Clean</div><div className="tiny subtle">Remove build output (bin, obj)</div></button>
          </div>
        </>
      )}
      {job && (
        <Modal title={job.title} icon="hammer" size="wide" onClose={() => setJob(null)} footer={<Button variant="primary" onClick={() => setJob(null)}>Close</Button>}>
          <JobView key={job.id} jobId={job.id} />
        </Modal>
      )}
    </div>
  )
}
