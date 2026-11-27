import { useState } from 'react'
import type { HistoryEntry } from '../types'
import { Icon } from './Icon'
import { Button, Empty, Modal, timeAgo } from './ui'

export function HistoryDialog({ history, onClose, onUndo }: { history: HistoryEntry[]; onClose: () => void; onUndo: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  return (
    <Modal
      title="Change history"
      subtitle="Every change QosriStudio makes is saved here. Undo goes back one step at a time."
      icon="history"
      onClose={onClose}
      footer={<>
        <Button onClick={onClose}>Close</Button>
        <Button variant="primary" icon="undo" disabled={history.length === 0} loading={busy} onClick={async () => { setBusy(true); try { await onUndo() } finally { setBusy(false) } }}>
          Undo last change
        </Button>
      </>}
    >
      {history.length === 0 ? (
        <Empty icon="history" title="No changes yet">Changes you make to this project will appear here.</Empty>
      ) : (
        <div className="col gap-6">
          {history.map((h, i) => (
            <div key={h.id} className="row" style={{ padding: '10px 12px', borderRadius: 10, background: i === 0 ? 'var(--accent-soft)' : 'var(--bg-sunken)' }}>
              <Icon name={h.description.startsWith('Added') ? 'plus' : h.description.startsWith('Deleted') ? 'trash' : 'edit'} size={16} />
              <span className="grow">{h.description}</span>
              <span className="tiny subtle">{timeAgo(h.time)}</span>
              {i === 0 && <span className="badge accent">Latest</span>}
            </div>
          ))}
        </div>
      )}
    </Modal>
  )
}
