import { useEffect, useState } from 'react'

export type Route =
  | { page: 'home' }
  | { page: 'project'; path: string }
  | { page: 'new' }

function parse(hash: string): Route {
  const h = hash.replace(/^#/, '')
  const [p, query = ''] = h.split('?')
  const params = new URLSearchParams(query)
  if (p === '/project' && params.get('path')) return { page: 'project', path: params.get('path')! }
  if (p === '/new') return { page: 'new' }
  return { page: 'home' }
}

export function href(route: Route): string {
  switch (route.page) {
    case 'project': return `#/project?path=${encodeURIComponent(route.path)}`
    case 'new': return '#/new'
    default: return '#/'
  }
}

export function navigate(route: Route) {
  window.location.hash = href(route)
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
