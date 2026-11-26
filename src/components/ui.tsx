import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Icon } from './Icon'

// ---------------------------------------------------------------- Buttons & small parts

type BtnProps = React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'danger' | 'ghost' | 'success' | 'default'
  size?: 'sm' | 'lg'
  icon?: string
  loading?: boolean
  iconOnly?: boolean
}

export function Button({ variant = 'default', size, icon, loading, iconOnly, children, className = '', ...rest }: BtnProps) {
  const cls = ['btn', variant !== 'default' && variant, size, iconOnly && 'icon', className].filter(Boolean).join(' ')
  return (
    <button className={cls} disabled={loading || rest.disabled} {...rest}>
      {loading ? <span className="spinner" /> : icon ? <Icon name={icon} size={size === 'sm' ? 15 : 17} /> : null}
      {children}
    </button>
  )
}

export function Spinner({ large }: { large?: boolean }) {
  return <span className={`spinner${large ? ' lg' : ''}`} />
}

export function MethodBadge({ method }: { method: string }) {
  return <span className={`method ${method}`}>{method}</span>
}

/** Shows a route with {placeholders} highlighted. */
export function RouteText({ route }: { route: string }) {
  const parts = route.split(/(\{[^}]+\})/g)
  return (
    <span className="route">
      {parts.map((p, i) => p.startsWith('{') ? <span key={i} className="param">{p}</span> : <span key={i}>{p}</span>)}
    </span>
  )
}

export function Switch({ checked, onChange, label, disabled }: { checked: boolean; onChange: (v: boolean) => void; label?: ReactNode; disabled?: boolean }) {
  return (
    <label className="switch" style={disabled ? { opacity: .5, cursor: 'not-allowed' } : undefined}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={e => onChange(e.target.checked)} />
      <span className="track" />
      {label && <span>{label}</span>}
    </label>
  )
}

export function Field({ label, hint, children, error }: { label: ReactNode; hint?: ReactNode; children: ReactNode; error?: string | null }) {
  return (
    <div className="field">
      <label>{label}</label>
      {children}
      {error ? <span className="hint" style={{ color: 'var(--danger)' }}>{error}</span> : hint ? <span className="hint">{hint}</span> : null}
    </div>
  )
}

export function Alert({ kind = 'info', children }: { kind?: 'info' | 'error' | 'warning' | 'success'; children: ReactNode }) {
  const icon = kind === 'error' ? 'alert' : kind === 'success' ? 'checkCircle' : kind === 'warning' ? 'alert' : 'info'
  return <div className={`alert ${kind}`}><Icon name={icon} size={17} /><div>{children}</div></div>
}

export function Empty({ icon, title, children, action }: { icon: string; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="empty">
      <div className="empty-icon"><Icon name={icon} size={26} /></div>
      <h3 style={{ fontSize: 16 }}>{title}</h3>
      {children && <p className="muted" style={{ maxWidth: 420 }}>{children}</p>}
      {action && <div className="mt-8">{action}</div>}
    </div>
  )
}

export function Segmented<T extends string>({ value, options, onChange }: { value: T; options: { value: T; label: ReactNode }[]; onChange: (v: T) => void }) {
  return (
    <div className="segmented" role="tablist">
      {options.map(o => (
        <button key={o.value} type="button" className={o.value === value ? 'active' : ''} onClick={() => onChange(o.value)}>{o.label}</button>
      ))}
    </div>
  )
}

export function Choice({ icon, title, desc, selected, onClick, disabled }: { icon: string; title: string; desc?: string; selected?: boolean; onClick: () => void; disabled?: boolean }) {
  return (
    <button type="button" className={`choice${selected ? ' selected' : ''}`} onClick={onClick} disabled={disabled} style={disabled ? { opacity: .45, cursor: 'not-allowed' } : undefined}>
      <span className="choice-icon"><Icon name={icon} size={18} /></span>
      <span>
        <div className="choice-title">{title}</div>
        {desc && <div className="choice-desc">{desc}</div>}
      </span>
    </button>
  )
}

// ---------------------------------------------------------------- Modal & Drawer

function useEscape(onClose: () => void) {
  useEffect(() => {
    const h = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', h)
    return () => window.removeEventListener('keydown', h)
  }, [onClose])
}

export function Modal({ title, subtitle, icon, onClose, children, footer, size }: {
  title: ReactNode; subtitle?: ReactNode; icon?: string; onClose: () => void; children: ReactNode; footer?: ReactNode; size?: 'wide' | 'narrow'
}) {
  useEscape(onClose)
  return (
    <div className="overlay" onMouseDown={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className={`modal ${size ?? ''}`} role="dialog" aria-modal="true">
        <div className="modal-header">
          {icon && <div className="group-icon" style={{ width: 36, height: 36 }}><Icon name={icon} size={19} /></div>}
          <div className="grow">
            <div className="modal-title">{title}</div>
            {subtitle && <div className="small subtle" style={{ marginTop: 2 }}>{subtitle}</div>}
          </div>
          <Button variant="ghost" iconOnly icon="x" size="sm" onClick={onClose} aria-label="Close" />
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-footer">{footer}</div>}
      </div>
    </div>
  )
}

export function Drawer({ onClose, children }: { onClose: () => void; children: ReactNode }) {
  useEscape(onClose)
  return (
    <>
      <div className="drawer-overlay" onClick={onClose} />
      <aside className="drawer" role="dialog" aria-modal="true">{children}</aside>
    </>
  )
}

// ---------------------------------------------------------------- Confirm dialog

type ConfirmOptions = { title: string; message: ReactNode; confirmText?: string; danger?: boolean }
const ConfirmContext = createContext<(o: ConfirmOptions) => Promise<boolean>>(async () => false)
export const useConfirm = () => useContext(ConfirmContext)

export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<(ConfirmOptions & { resolve: (v: boolean) => void }) | null>(null)
  const confirm = useCallback((o: ConfirmOptions) => new Promise<boolean>(resolve => setState({ ...o, resolve })), [])
  const close = (v: boolean) => { state?.resolve(v); setState(null) }
  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      {state && (
        <Modal
          size="narrow"
          title={state.title}
          icon={state.danger ? 'alert' : 'info'}
          onClose={() => close(false)}
          footer={<>
            <Button onClick={() => close(false)}>Cancel</Button>
            <Button variant={state.danger ? 'danger' : 'primary'} onClick={() => close(true)} autoFocus>{state.confirmText ?? 'Confirm'}</Button>
          </>}
        >
          <div className="muted">{state.message}</div>
        </Modal>
      )}
    </ConfirmContext.Provider>
  )
}

// ---------------------------------------------------------------- Toasts

type Toast = { id: number; kind: 'success' | 'error' | 'info'; text: ReactNode; action?: { label: string; onClick: () => void } }
type ToastApi = {
  success: (text: ReactNode, action?: Toast['action']) => void
  error: (text: ReactNode) => void
  info: (text: ReactNode) => void
}
const ToastContext = createContext<ToastApi>({ success: () => {}, error: () => {}, info: () => {} })
export const useToast = () => useContext(ToastContext)

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(1)
  const push = useCallback((kind: Toast['kind'], text: ReactNode, action?: Toast['action']) => {
    const id = nextId.current++
    setToasts(t => [...t.slice(-3), { id, kind, text, action }])
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), kind === 'error' ? 7000 : 4500)
  }, [])
  const toastApi = useMemo<ToastApi>(() => ({
    success: (t, a) => push('success', t, a),
    error: t => push('error', t),
    info: t => push('info', t),
  }), [push])
  return (
    <ToastContext.Provider value={toastApi}>
      {children}
      <div className="toasts" aria-live="polite">
        {toasts.map(t => (
          <div key={t.id} className={`toast ${t.kind}`}>
            <span className="toast-icon"><Icon name={t.kind === 'success' ? 'checkCircle' : t.kind === 'error' ? 'alert' : 'info'} size={18} /></span>
            <span>{t.text}</span>
            {t.action && (
              <Button size="sm" variant="ghost" className="action" onClick={() => { t.action!.onClick(); setToasts(x => x.filter(y => y.id !== t.id)) }}>{t.action.label}</Button>
            )}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}

// ---------------------------------------------------------------- Theme

export type Theme = 'system' | 'light' | 'dark'

export function useTheme(): [Theme, (t: Theme) => void] {
  const [theme, setTheme] = useState<Theme>(() => {
    try { return (localStorage.getItem('es-theme') as Theme) || 'system' } catch { return 'system' }
  })
  useEffect(() => {
    const root = document.documentElement
    if (theme === 'system') root.removeAttribute('data-theme')
    else root.setAttribute('data-theme', theme)
    try { localStorage.setItem('es-theme', theme) } catch { /* ignore */ }
  }, [theme])
  return [theme, setTheme]
}

// ---------------------------------------------------------------- Helpers

export function timeAgo(iso: string): string {
  const s = Math.max(1, Math.round((Date.now() - new Date(iso).getTime()) / 1000))
  if (s < 60) return 'just now'
  const m = Math.round(s / 60); if (m < 60) return `${m} min ago`
  const h = Math.round(m / 60); if (h < 24) return `${h} hour${h > 1 ? 's' : ''} ago`
  const d = Math.round(h / 24); if (d < 30) return `${d} day${d > 1 ? 's' : ''} ago`
  return new Date(iso).toLocaleDateString()
}

export function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}
