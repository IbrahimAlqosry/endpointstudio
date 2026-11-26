import { useEffect, useState } from 'react'

export type Route =
  | { page: 'home' }
  | { page: 'project'; path: string }
  | { page: 'solution'; path: string }
  | { page: 'new' }
  | { page: 'recover' }
  | { page: 'frontend'; path: string }

function parse(hash: string): Route {
  const h = hash.replace(/^#/, '')
  const [p, query = ''] = h.split('?')
  const params = new URLSearchParams(query)
  if (p === '/project' && params.get('path')) return { page: 'project', path: params.get('path')! }
  if (p === '/solution' && params.get('path')) return { page: 'solution', path: params.get('path')! }
  if (p === '/new') return { page: 'new' }
  if (p === '/recover') return { page: 'recover' }
  if (p === '/frontend' && params.get('path')) return { page: 'frontend', path: params.get('path')! }
  return { page: 'home' }
}

export function href(route: Route): string {
  switch (route.page) {
    case 'project': return `#/project?path=${encodeURIComponent(route.path)}`
    case 'solution': return `#/solution?path=${encodeURIComponent(route.path)}`
    case 'new': return '#/new'
    case 'recover': return '#/recover'
    case 'frontend': return `#/frontend?path=${encodeURIComponent(route.path)}`
    default: return '#/'
  }
}

export function navigate(route: Route) {
  window.location.hash = href(route)
}

/** Opens a project file or a solution file on the right page. */
export function openPath(path: string) {
  navigate(routeFor(path))
}

/** Solutions, .NET projects and Angular/React apps each open on their own page. */
export function routeFor(path: string): Route {
  if (/\.slnx?$/i.test(path)) return { page: 'solution', path }
  if (/(package|angular)\.json$/i.test(path)) return { page: 'frontend', path }
  return { page: 'project', path }
}

export function useRoute(): Route {
  const [route, setRoute] = useState<Route>(() => parse(window.location.hash))
  useEffect(() => {
    const onChange = () => setRoute(parse(window.location.hash))
    window.addEventListener('hashchange', onChange)
    return () => window.removeEventListener('hashchange', onChange)
  }, [])
  return route
}
