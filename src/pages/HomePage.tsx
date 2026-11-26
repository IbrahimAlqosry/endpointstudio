import { Icon } from '../components/Icon'
import { Button, Empty, timeAgo } from '../components/ui'
import { navigate } from '../router'
import type { RecentProject } from '../types'

export function HomePage({ recent, onOpen, onForget }: { recent: RecentProject[]; onOpen: () => void; onForget: (path: string) => void }) {
  return (
    <div className="page">
      <section className="hero">
        <div className="row gap-6" style={{ marginBottom: 10 }}>
          <span className="badge accent"><Icon name="sparkles" size={13} /> No code needed</span>
        </div>
        <h1>Design your .NET APIs visually</h1>
        <p>Open any .NET API project to browse, add, edit and remove endpoints, or turn a database into a ready-to-run API in a few clicks.</p>
        <div className="row wrap gap-6 mt-16">
          {['.NET Framework 4.x', '.NET Core 2.x–3.1', '.NET 5', '.NET 6', '.NET 7', '.NET 8', '.NET 9', '.NET 10', '.NET 11'].map(v => <span key={v} className="badge">{v}</span>)}
        </div>
        <div className="row wrap mt-24">
          <Button variant="primary" size="lg" icon="folderOpen" onClick={onOpen}>Open a project</Button>
          <Button size="lg" icon="database" onClick={() => navigate({ page: 'new' })}>Create from a database</Button>
        </div>
      </section>

      <div className="action-cards">
        <div className="card action-card">
          <div className="ac-icon c1"><Icon name="layers" size={22} /></div>
          <h3 style={{ fontSize: 15 }}>Browse & edit endpoints</h3>
          <p className="muted small">See every endpoint grouped and searchable. Add new ones from simple forms and change paths, names and access in one place.</p>
        </div>
        <div className="card action-card">
          <div className="ac-icon c2"><Icon name="play" size={22} /></div>
          <h3 style={{ fontSize: 15 }}>Run & try instantly</h3>
          <p className="muted small">Start your API with one click and send test requests from a form. Check the build and export a Postman collection.</p>
        </div>
        <div className="card action-card">
          <div className="ac-icon c3"><Icon name="undo" size={22} /></div>
          <h3 style={{ fontSize: 15 }}>Safe by design</h3>
          <p className="muted small">Every change is checked before it’s saved and kept in a history, so you can undo anything with one click.</p>
        </div>
      </div>

      <div className="section-title"><Icon name="clock" size={17} /> Recent projects</div>
      {recent.length === 0 ? (
        <div className="card">
          <Empty icon="folderOpen" title="No projects yet" action={<Button variant="primary" icon="folderOpen" onClick={onOpen}>Open a project</Button>}>
            Projects you open or create will show up here for quick access.
          </Empty>
        </div>
      ) : (
        <div className="project-grid">
          {recent.map(p => (
            <div key={p.path} className="card hoverable project-card" onClick={() => navigate({ page: 'project', path: p.path })}>
              <button
                className="btn icon sm ghost remove"
                title="Remove from recent"
                onClick={e => { e.stopPropagation(); onForget(p.path) }}
              ><Icon name="x" size={15} /></button>
              <div className="row">
                <div className="group-icon"><Icon name="box" size={17} /></div>
                <div className="grow">
                  <div className="strong truncate">{p.name}</div>
                  <div className="tiny subtle">Opened {timeAgo(p.lastOpened)}</div>
                </div>
              </div>
              <div className="tiny subtle truncate" title={p.path}>{folderOf(p.path)}</div>
              <div className="row">
                <span className="badge accent">{p.endpointCount} endpoint{p.endpointCount === 1 ? '' : 's'}</span>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function folderOf(path: string) {
  const i = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
  return i > 0 ? path.slice(0, i) : path
}
