import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { Icon } from './components/Icon'
import { FolderPicker } from './components/FolderPicker'
import { CloneDialog } from './components/GitPanel'
import { CommandPalette } from './components/CommandPalette'
import type { PaletteItem } from './palette'
import { ConfirmProvider, Segmented, ToastProvider, useTheme, type Theme } from './components/ui'
import { HomePage } from './pages/HomePage'
import { NewProjectPage } from './pages/NewProjectPage'
import { ProjectPage } from './pages/ProjectPage'
import { SolutionPage } from './pages/SolutionPage'
import { RecoverPage } from './pages/RecoverPage'
import { FrontendPage } from './pages/FrontendPage'
import { DbStudioPage } from './pages/DbStudioPage'
import { href, openPath, routeFor, useRoute } from './router'
import type { RecentProject, SavedConnection } from './types'

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
  const [cloneOpen, setCloneOpen] = useState(false)
  const [pickerStart, setPickerStart] = useState<string | undefined>()
  const [defaultFolder, setDefaultFolder] = useState('')
  useEffect(() => { api.system().then(s => setDefaultFolder(s.defaultLocation)).catch(() => {}) }, [])
  const [connections, setConnections] = useState<SavedConnection[]>([])
  useEffect(() => { api.dbsConnections().then(setConnections).catch(() => {}) }, [route])

  const paletteBase = useMemo<PaletteItem[]>(() => {
    const go = (h: string) => () => { window.location.hash = h }
    const items: PaletteItem[] = [
      { id: 'p-home', group: 'Go to', title: 'Home', icon: 'home', run: go(href({ page: 'home' })) },
      { id: 'p-open', group: 'Go to', title: 'Open a project…', icon: 'folderOpen', keywords: 'browse solution csproj angular react', run: () => { setPickerStart(undefined); setPicker(true) } },
      { id: 'p-clone', group: 'Go to', title: 'Copy a project from Git…', icon: 'download', keywords: 'clone github gitlab repository', run: () => setCloneOpen(true) },
      { id: 'p-db', group: 'Go to', title: 'Database Studio', icon: 'table', keywords: 'sql tables diagram data', run: go(href({ page: 'db' })) },
      { id: 'p-new', group: 'Go to', title: 'New API from a database', icon: 'database', keywords: 'generate create clean architecture jwt', run: go(href({ page: 'new' })) },
      { id: 'p-recover', group: 'Go to', title: 'Recover code from DLLs', icon: 'wand', keywords: 'decompile reverse', run: go(href({ page: 'recover' })) },
      { id: 't-light', group: 'Appearance', title: 'Light theme', icon: 'sun', run: () => setTheme('light') },
      { id: 't-dark', group: 'Appearance', title: 'Dark theme', icon: 'moon', run: () => setTheme('dark') },
      { id: 't-auto', group: 'Appearance', title: 'Match the system theme', icon: 'monitor', run: () => setTheme('system') },
    ]
    for (const r of recent) items.push({ id: 'r-' + r.path, group: 'Recent projects', title: r.name, subtitle: r.path, icon: /\.slnx?$/i.test(r.path) ? 'layers' : /package\.json$/i.test(r.path) ? 'monitor' : 'box', run: () => openPath(r.path) })
    for (const c of connections) items.push({ id: 'c-' + c.id, group: 'Databases', title: c.name, subtitle: c.info.provider, icon: 'database', run: go(href({ page: 'db', id: c.id })) })
    return items
  }, [recent, connections, setTheme])

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
            <div className="brand-name" style={{ color: 'var(--text)' }}>QosriStudio</div>
            <div className="brand-sub">.NET, web apps & databases</div>
          </div>
        </a>

        <button className="nav-search" onClick={() => window.dispatchEvent(new Event('sw-palette'))}>
          <Icon name="search" size={15} /><span className="grow" style={{ textAlign: 'left' }}>Search…</span><kbd>Ctrl</kbd><kbd>K</kbd>
        </button>
        <nav className="nav">
          <a className={`nav-item${route.page === 'home' ? ' active' : ''}`} href={href({ page: 'home' })}><Icon name="home" size={17} /> Home</a>
          <button className="nav-item" onClick={() => { setPickerStart(undefined); setPicker(true) }}><Icon name="folderOpen" size={17} /> Open project…</button>
          <button className="nav-item" onClick={() => setCloneOpen(true)}><Icon name="download" size={17} /> Copy from Git…</button>
          <a className={`nav-item${route.page === 'db' ? ' active' : ''}`} href={href({ page: 'db' })}><Icon name="table" size={17} /> Database Studio</a>
          <a className={`nav-item${route.page === 'new' ? ' active' : ''}`} href={href({ page: 'new' })}><Icon name="database" size={17} /> New from database</a>
          <a className={`nav-item${route.page === 'recover' ? ' active' : ''}`} href={href({ page: 'recover' })}><Icon name="wand" size={17} /> Recover code</a>
        </nav>

        {recent.length > 0 && (
          <nav className="nav">
            <div className="nav-label">Recent projects</div>
            {recent.slice(0, 8).map(p => (
              <a
                key={p.path}
                className={`nav-item${(route.page === 'project' || route.page === 'solution' || route.page === 'frontend') && route.path.toLowerCase() === p.path.toLowerCase() ? ' active' : ''}`}
                href={href(routeFor(p.path))}
                title={p.path}
              >
                <Icon name={/\.slnx?$/i.test(p.path) ? 'layers' : /package\.json$/i.test(p.path) ? 'monitor' : 'box'} size={16} />
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
          <span className="strong">QosriStudio</span>
        </div>
        {route.page === 'home' && <HomePage recent={recent} onOpen={() => setPicker(true)} onForget={async p => { await api.forget(p); refreshRecent() }} />}
        {route.page === 'project' && <ProjectPage key={route.path} path={route.path} onChanged={refreshRecent} />}
        {route.page === 'solution' && <SolutionPage key={route.path} path={route.path} onChanged={refreshRecent} />}
        {route.page === 'new' && <NewProjectPage />}
        {route.page === 'recover' && <RecoverPage />}
        {route.page === 'db' && <DbStudioPage key={(route.id ?? '') + (route.database ?? '')} id={route.id} database={route.database} />}
        {route.page === 'frontend' && <FrontendPage key={route.path} path={route.path} onChanged={refreshRecent} />}
      </main>

      <CommandPalette base={paletteBase} />
      {picker && <FolderPicker mode="project" initialPath={pickerStart} onPick={openProject} onClose={() => setPicker(false)} />}
      {cloneOpen && <CloneDialog defaultFolder={defaultFolder} onClose={() => setCloneOpen(false)} onCloned={folder => { setCloneOpen(false); setPickerStart(folder); setPicker(true) }} />}
    </div>
  )
}
