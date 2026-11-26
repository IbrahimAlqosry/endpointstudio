import type { BuildResult } from '../types'
import { Icon } from './Icon'
import { Button, Modal, Spinner } from './ui'

export function BuildDialog({ result, onClose, onRetry }: { result: BuildResult | 'loading'; onClose: () => void; onRetry: () => void }) {
  if (result === 'loading') {
    return (
      <Modal title="Checking the build" icon="hammer" onClose={onClose} size="narrow">
        <div className="empty"><Spinner large /><span className="muted">Compiling your project. This usually takes a few seconds…</span></div>
      </Modal>
    )
  }
  return (
    <Modal
      title={result.success ? 'Everything builds correctly' : 'The project has problems'}
      subtitle={`Checked in ${(result.durationMs / 1000).toFixed(1)}s${result.warningCount ? ` · ${result.warningCount} warning${result.warningCount > 1 ? 's' : ''}` : ''}`}
      icon={result.success ? 'checkCircle' : 'alert'}
      onClose={onClose}
      footer={<>
        <Button icon="refresh" onClick={onRetry}>Check again</Button>
        <Button variant="primary" onClick={onClose}>Done</Button>
      </>}
    >
      {result.success ? (
        <div className="empty" style={{ padding: '20px 0' }}>
          <div className="success-burst"><Icon name="check" size={34} stroke={2.5} /></div>
          <p className="muted">Your project compiled without errors and is ready to run.</p>
        </div>
      ) : (
        <div className="col">
          <p className="muted small">{result.errorCount} problem{result.errorCount > 1 ? 's' : ''} found. If they started after a recent change, you can undo it from History.</p>
          <div className="table-wrap" style={{ maxHeight: 360 }}>
            <table className="table">
              <thead><tr><th>Where</th><th>Problem</th></tr></thead>
              <tbody>
                {result.errors.map((e, i) => (
                  <tr key={i}>
                    <td className="small" style={{ whiteSpace: 'nowrap' }}>{e.file ? <>{e.file.split('/').pop()}{e.line ? <span className="subtle"> · line {e.line}</span> : null}</> : <span className="subtle">Project</span>}</td>
                    <td className="small">{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </Modal>
  )
}
