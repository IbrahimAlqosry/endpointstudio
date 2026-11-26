import { useEffect, useState } from 'react'
import { api } from '../api'
import type { HealthReport } from '../types'
import { Icon } from './Icon'
import { Alert, Button, Modal, Spinner, errorText } from './ui'

const SEVERITY: Record<string, { label: string; cls: string; icon: string }> = {
  high: { label: 'Fix soon', cls: 'danger', icon: 'alert' },
  medium: { label: 'Important', cls: 'warning', icon: 'alert' },
  low: { label: 'Nice to fix', cls: 'accent', icon: 'info' },
  info: { label: 'Good to know', cls: '', icon: 'info' },
}

export function HealthDialog({ path, onClose, onOpenEndpoint, onUpgrade }: {
  path: string
  onClose: () => void
  onOpenEndpoint: (id: string) => void
  onUpgrade: () => void
}) {
  const [report, setReport] = useState<HealthReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<number | null>(0)

  useEffect(() => { api.health(path).then(setReport).catch(e => setError(errorText(e))) }, [path])

  const color = !report ? 'var(--text-3)' : report.score >= 90 ? 'var(--success)' : report.score >= 75 ? 'var(--accent)' : report.score >= 55 ? 'var(--warning)' : 'var(--danger)'

  return (
    <Modal title="Health check" subtitle="Security, reliability and maintenance problems found in this project." icon="checkCircle" onClose={onClose} size="wide"
      footer={<Button variant="primary" onClick={onClose}>Done</Button>}>
      {error && <Alert kind="error">{error}</Alert>}
      {!report && !error && <div className="empty"><Spinner large /><span className="muted">Checking the project…</span></div>}
      {report && (
        <div className="col gap-20">
          <div className="row gap-20" style={{ alignItems: 'center' }}>
            <div style={{ position: 'relative', width: 96, height: 96, flexShrink: 0 }}>
              <svg viewBox="0 0 36 36" width="96" height="96" style={{ transform: 'rotate(-90deg)' }}>
                <circle cx="18" cy="18" r="15.9" fill="none" stroke="var(--bg-sunken)" strokeWidth="3.2" />
                <circle cx="18" cy="18" r="15.9" fill="none" stroke={color} strokeWidth="3.2" strokeLinecap="round"
                  strokeDasharray={`${report.score} ${100 - report.score}`} />
              </svg>
              <div style={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', fontWeight: 800, fontSize: 24 }}>{report.score}</div>
            </div>
            <div>
              <div style={{ fontSize: 20, fontWeight: 800, color }}>{report.grade}</div>
              <div className="muted small">
                {report.findings.filter(f => f.severity === 'high').length} to fix soon · {report.findings.filter(f => f.severity === 'medium').length} important · {report.findings.filter(f => f.severity === 'low' || f.severity === 'info').length} suggestions
              </div>
              <div className="tiny subtle mt-8">{report.checks} checks run. Nothing in your project was changed.</div>
            </div>
          </div>

          {report.findings.length === 0 && <Alert kind="success">No problems found. Nice work!</Alert>}

          <div className="col gap-6">
            {report.findings.map((f, i) => {
              const sev = SEVERITY[f.severity] ?? SEVERITY.info
              const isOpen = open === i
              return (
                <div key={i} className="card" style={{ overflow: 'hidden' }}>
                  <div className="group-header" onClick={() => setOpen(isOpen ? null : i)} style={{ borderBottom: isOpen ? undefined : 'none' }}>
                    <Icon name={isOpen ? 'chevronDown' : 'chevronRight'} size={15} />
                    <span className={`badge ${sev.cls}`} style={{ minWidth: 92, justifyContent: 'center' }}>{sev.label}</span>
                    <div className="grow" style={{ minWidth: 0 }}>
                      <div className="strong">{f.title}</div>
                      <div className="tiny subtle">{f.category}</div>
                    </div>
                  </div>
                  {isOpen && (
                    <div className="col" style={{ padding: '12px 16px 16px' }}>
                      <p className="muted small">{f.detail}</p>
                      {f.items.length > 0 && (
                        <div className="col gap-4">
                          {f.items.map((item, j) => {
                            const id = f.endpointIds?.[j]
                            return (
                              <div key={j} className="row small" style={{ padding: '6px 10px', background: 'var(--bg-sunken)', borderRadius: 8 }}>
                                <span className="mono grow truncate" title={item}>{item}</span>
                                {id && <Button size="sm" variant="ghost" onClick={() => { onOpenEndpoint(id); onClose() }}>Open</Button>}
                              </div>
                            )
                          })}
                          {f.endpointIds && f.endpointIds.length > f.items.length && <span className="tiny subtle">…and {f.endpointIds.length - f.items.length} more</span>}
                        </div>
                      )}
                      {f.category === 'Maintenance' && f.title.includes('security updates') && (
                        <div><Button size="sm" variant="primary" icon="rocket" onClick={() => { onClose(); onUpgrade() }}>Open the upgrade assistant</Button></div>
                      )}
                    </div>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      )}
    </Modal>
  )
}
