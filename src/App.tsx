import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { Icon } from './components/Icon'
import { FolderPicker } from './components/FolderPicker'
import { ConfirmProvider, Segmented, ToastProvider, useTheme, type Theme } from './components/ui'
import { HomePage } from './pages/HomePage'
import { NewProjectPage } from './pages/NewProjectPage'
import { ProjectPage } from './pages/ProjectPage'
import { SolutionPage } from './pages/SolutionPage'
import { href, openPath, routeFor, useRoute } from './router'
import type { RecentProject } from './types'

export default function App() {
  return (
    <ToastProvider>
      <ConfirmProvider>
        <Shell />
      </ConfirmProvider>
    </ToastProvider>
  )
}

function Shell() {
  const route = useRoute()
  const [theme, setTheme] = useTheme()
  const [recent, setRecent] = useState<RecentProject[]>([])
  const [picker, setPicker] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)

  const refreshRecent = useCallback(() => { api.recent().then(setRecent).catch(() => {}) }, [])
  useEffect(() => { refreshRecent() }, [refreshRecent, route])
  useEffect(() => { setMenuOpen(false) }, [route])

  const openProject = (path: string) => { setPicker(false); openPath(path) }

  return (
    <div className="app">
      <aside className={`sidebar${menuOpen ? ' open' : ''}`}>
        <a className="brand" href={href({ page: 'home' })}>
          <div className="brand-logo"><Icon name="logo" size={19} stroke={2.2} /></div>
          <div>
            <div className="brand-name" style={{ color: 'var(--text)' }}>Endpoint Studio</div>
            <div className="brand-sub">Build APIs without code</div>
          </div>
        </a>

        <nav className="nav">
          <a className={`nav-item${route.page === 'home' ? ' active' : ''}`} href={href({ page: 'home' })}><Icon name="home" size={17} /> Home</a>
          <button className="nav-item" onClick={() => setPicker(true)}><Icon name="folderOpen" size={17} /> Open project…</button>
          <a className={`nav-item${route.page === 'new' ? ' active' : ''}`} href={href({ page: 'new' })}><Icon name="database" size={17} /> New from database</a>
        </nav>

        {recent.length > 0 && (
          <nav className="nav">
            <div className="nav-label">Recent projects</div>
            {recent.slice(0, 8).map(p => (
              <a
                key={p.path}
                className={`nav-item${(route.page === 'project' || route.page === 'solution') && route.path.toLowerCase() === p.path.toLowerCase() ? ' active' : ''}`}
                href={href(routeFor(p.path))}
                title={p.path}
              >
                <Icon name={/\.slnx?$/i.test(p.path) ? 'layers' : 'box'} size={16} />
                <span className="truncate">{p.name}</span>
                <span className="count">{p.endpointCount}</span>
              </a>
            ))}
          </nav>
        )}

        <div className="sidebar-footer">
          <Segmented<Theme>
            value={theme}
            onChange={setTheme}
            options={[
              { value: 'light', label: <span className="row gap-4"><Icon name="sun" size={14} />Light</span> },
              { value: 'dark', label: <span className="row gap-4"><Icon name="moon" size={14} />Dark</span> },
              { value: 'system', label: <span className="row gap-4"><Icon name="monitor" size={14} />Auto</span> },
            ]}
          />
        </div>
      </aside>

      <main className="main">
        <div className="topbar-mobile">
          <button className="btn icon sm ghost" onClick={() => setMenuOpen(o => !o)} aria-label="Menu"><Icon name="menu" /></button>
          <div className="brand-logo" style={{ width: 28, height: 28 }}><Icon name="logo" size={16} stroke={2.2} /></div>
          <span className="strong">Endpoint Studio</span>
        </div>
        {route.page === 'home' && <HomePage recent={recent} onOpen={() => setPicker(true)} onForget={async p => { await api.forget(p); refreshRecent() }} />}
        {route.page === 'project' && <ProjectPage key={route.path} path={route.path} onChanged={refreshRecent} />}
        {route.page === 'solution' && <SolutionPage key={route.path} path={route.path} onChanged={refreshRecent} />}
        {route.page === 'new' && <NewProjectPage />}
      </main>

      {picker && <FolderPicker mode="project" onPick={openProject} onClose={() => setPicker(false)} />}
    </div>
  )
}
