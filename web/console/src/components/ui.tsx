import { clsx } from 'clsx'
import { Children, isValidElement, useEffect, useRef, useState, type ButtonHTMLAttributes, type ChangeEvent, type InputHTMLAttributes, type KeyboardEvent as ReactKeyboardEvent, type ReactNode, type SelectHTMLAttributes } from 'react'
import { createPortal } from 'react-dom'
import { AlertTriangle, Check, CheckCircle2, ChevronDown, Info, Loader2, OctagonAlert, X } from 'lucide-react'

export const cx = clsx

export function Card({ className, children, title, subtitle, actions, pad = true }: {
  className?: string; children?: ReactNode; title?: ReactNode; subtitle?: ReactNode; actions?: ReactNode; pad?: boolean
}) {
  return (
    <section className={cx('rounded-lg border border-line bg-panel text-fg shadow-card animate-rise min-w-0', className)}>
      {(title || actions) && (
        <header className="flex items-start justify-between gap-3 px-4 pt-3">
          <div className="min-w-0">
            {title && <h2 className="text-sm leading-none font-semibold text-fg">{title}</h2>}
            {subtitle && <p className="mt-1 text-xs text-muted">{subtitle}</p>}
          </div>
          {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={cx(pad && 'px-4 py-3', pad && (title || actions) && 'pt-2.5', !pad && (title || actions) && 'mt-2.5')}>{children}</div>
    </section>
  )
}

type Variant = 'primary' | 'secondary' | 'ghost' | 'danger'
export function Button({ variant = 'secondary', size = 'md', loading, icon, className, children, ...rest }:
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; size?: 'sm' | 'md'; loading?: boolean; icon?: ReactNode }) {
  return (
    <button
      {...rest}
      disabled={rest.disabled || loading}
      className={cx(
        'inline-flex items-center justify-center gap-2 rounded-md font-medium transition-all select-none whitespace-nowrap [&_svg]:shrink-0',
        'outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-50 disabled:pointer-events-none',
        size === 'sm' ? 'h-7 px-2.5 text-xs gap-1.5' : 'h-8 px-3 text-sm',
        variant === 'primary' && 'bg-brand text-white shadow-xs hover:bg-brand/90',
        variant === 'secondary' && 'border border-line-strong bg-panel text-fg shadow-xs hover:bg-panel-2 dark:bg-white/4.5 dark:hover:bg-white/10',
        variant === 'ghost' && 'text-fg hover:bg-panel-2',
        variant === 'danger' && 'bg-critical text-white shadow-xs hover:bg-critical/90',
        className,
      )}
    >
      {loading ? <Loader2 className="size-4 animate-spin" /> : icon}
      {children}
    </button>
  )
}

export function Input({ className, ...rest }: InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      {...rest}
      className={cx(
        'h-8 w-full min-w-0 rounded-md border border-line-strong bg-transparent px-2.5 py-1 text-sm text-fg shadow-xs placeholder:text-muted dark:bg-white/4.5',
        'outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:opacity-50',
        className,
      )}
    />
  )
}

interface SelectOption { value: string; label: ReactNode; disabled?: boolean }

function textOf(n: ReactNode): string {
  if (n == null || typeof n === 'boolean') return ''
  if (typeof n === 'string' || typeof n === 'number') return String(n)
  if (Array.isArray(n)) return n.map(textOf).join('')
  if (isValidElement<{ children?: ReactNode }>(n)) return textOf(n.props.children)
  return ''
}

function collectOptions(children: ReactNode, out: SelectOption[] = []): SelectOption[] {
  Children.forEach(children, (c) => {
    if (!isValidElement<{ children?: ReactNode; value?: string | number; disabled?: boolean }>(c)) return
    if (c.type === 'option') out.push({ value: c.props.value != null ? String(c.props.value) : textOf(c.props.children), label: c.props.children, disabled: c.props.disabled })
    else collectOptions(c.props.children, out)
  })
  return out
}

/** Themed drop-down with the same API as a native <select> (value / onChange(e.target.value) / <option> children).
 *  The list is drawn by us rather than the browser, so it follows the light/dark theme. */
export function Select({ className, children, value, onChange, disabled, id, 'aria-label': ariaLabel }: SelectHTMLAttributes<HTMLSelectElement>) {
  const options = collectOptions(children)
  const current = String(value ?? '')
  const selected = options.find((o) => o.value === current) ?? options[0]
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const [pos, setPos] = useState<{ left: number; top?: number; bottom?: number; width: number } | null>(null)
  const btn = useRef<HTMLButtonElement>(null)
  const list = useRef<HTMLDivElement>(null)

  const place = () => {
    const r = btn.current?.getBoundingClientRect()
    if (!r) return
    const up = window.innerHeight - r.bottom < 240 && r.top > window.innerHeight - r.bottom
    setPos(up ? { left: r.left, bottom: window.innerHeight - r.top + 4, width: r.width } : { left: r.left, top: r.bottom + 4, width: r.width })
  }
  const show = () => {
    if (disabled) return
    place(); setOpen(true)
    setActive(Math.max(0, options.findIndex((o) => o.value === selected?.value)))
  }
  const choose = (o: SelectOption) => {
    if (o.disabled) return
    setOpen(false); btn.current?.focus()
    if (o.value !== current) onChange?.({ target: { value: o.value }, currentTarget: { value: o.value } } as unknown as ChangeEvent<HTMLSelectElement>)
  }

  useEffect(() => {
    if (!open) return
    const away = (e: MouseEvent) => { if (!btn.current?.contains(e.target as Node) && !list.current?.contains(e.target as Node)) setOpen(false) }
    const move = (e: Event) => { if (!list.current?.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', away)
    window.addEventListener('resize', move)
    window.addEventListener('scroll', move, true)
    return () => { document.removeEventListener('mousedown', away); window.removeEventListener('resize', move); window.removeEventListener('scroll', move, true) }
  }, [open])
  useEffect(() => { if (open) list.current?.querySelector<HTMLElement>(`[data-i="${active}"]`)?.scrollIntoView({ block: 'nearest' }) }, [open, active])

  const step = (from: number, dir: 1 | -1) => {
    for (let i = from + dir; i >= 0 && i < options.length; i += dir) if (!options[i].disabled) return i
    return from
  }
  const onKey = (e: ReactKeyboardEvent<HTMLButtonElement>) => {
    if (!open) {
      if (['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(e.key)) { e.preventDefault(); show() }
      return
    }
    if (e.key === 'Escape' || e.key === 'Tab') { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation() } setOpen(false) }
    else if (e.key === 'ArrowDown') { e.preventDefault(); setActive((a) => step(a, 1)) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((a) => step(a, -1)) }
    else if (e.key === 'Home') { e.preventDefault(); setActive(step(-1, 1)) }
    else if (e.key === 'End') { e.preventDefault(); setActive(step(options.length, -1)) }
    else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); if (options[active]) choose(options[active]) }
    else if (e.key.length === 1) {
      const k = e.key.toLowerCase()
      const i = options.findIndex((o, j) => j > active && !o.disabled && textOf(o.label).toLowerCase().startsWith(k))
      const first = options.findIndex((o) => !o.disabled && textOf(o.label).toLowerCase().startsWith(k))
      if (i >= 0 || first >= 0) setActive(i >= 0 ? i : first)
    }
  }

  return (
    <>
      <button
        ref={btn} type="button" id={id} disabled={disabled} aria-label={ariaLabel} aria-haspopup="listbox" aria-expanded={open}
        onClick={(e) => { e.preventDefault(); if (open) setOpen(false); else show() }} onKeyDown={onKey}
        className={cx(
          'inline-flex h-8 min-w-0 items-center justify-between gap-2 rounded-md border border-line-strong bg-transparent pl-2.5 pr-2 text-left text-sm text-fg shadow-xs outline-none transition-[color,box-shadow] dark:bg-white/4.5',
          'hover:bg-panel-2 dark:hover:bg-white/8 focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-50',
          open && 'border-ring ring-[3px] ring-ring/50',
          className,
        )}
      >
        <span className="truncate">{selected?.label}</span>
        <ChevronDown className={cx('size-4 shrink-0 text-muted transition-transform', open && 'rotate-180')} />
      </button>
      {open && pos && createPortal(
        <div
          ref={list} role="listbox" onMouseDown={(e) => e.stopPropagation()}
          style={{ left: pos.left, top: pos.top, bottom: pos.bottom, minWidth: pos.width }}
          className="fixed z-70 max-h-60 max-w-[min(28rem,calc(100vw-2rem))] overflow-y-auto rounded-md border border-line bg-panel p-1 text-sm text-fg shadow-lg animate-rise"
        >
          {options.map((o, i) => (
            <div
              key={`${o.value}-${i}`} data-i={i} role="option" aria-selected={o.value === selected?.value} aria-disabled={o.disabled}
              onMouseEnter={() => !o.disabled && setActive(i)} onClick={() => choose(o)}
              className={cx(
                'flex cursor-default select-none items-center gap-2 rounded-sm py-1.5 pl-2 pr-8 relative',
                i === active && !o.disabled && 'bg-panel-2 dark:bg-white/8',
                o.disabled && 'opacity-50',
              )}
            >
              <span className="truncate">{o.label}</span>
              {o.value === selected?.value && <Check className="absolute right-2 size-4 text-brand" />}
            </div>
          ))}
        </div>,
        document.body,
      )}
    </>
  )
}

export function Field({ label, hint, children, className }: { label: string; hint?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <label className={cx('block', className)}>
      <span className="mb-1.5 block text-[13px] font-medium leading-none text-fg">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-xs text-muted">{hint}</span>}
    </label>
  )
}

export function Switch({ checked, onChange, label, description, disabled }: {
  checked: boolean; onChange: (v: boolean) => void; label?: ReactNode; description?: ReactNode; disabled?: boolean
}) {
  return (
    <label className={cx('flex items-start gap-3', disabled ? 'opacity-50' : 'cursor-pointer')}>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={cx(
          'relative mt-0.5 inline-flex h-[1.15rem] w-8 shrink-0 items-center rounded-full border border-transparent shadow-xs transition-all outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50',
          checked ? 'bg-brand' : 'bg-[#e5e5e5] dark:bg-white/15',
        )}
      >
        <span className={cx('pointer-events-none block size-4 rounded-full bg-panel ring-0 transition-transform dark:bg-fg', checked ? 'translate-x-[calc(100%-2px)] bg-white dark:bg-white' : 'translate-x-0')} />
      </button>
      {(label || description) && (
        <span className="min-w-0">
          {label && <span className="block text-sm font-medium text-fg">{label}</span>}
          {description && <span className="block text-xs text-muted">{description}</span>}
        </span>
      )}
    </label>
  )
}

const tone = {
  neutral: 'bg-transparent text-fg ring-line-strong',
  brand: 'bg-brand-soft text-brand-ink ring-brand/25',
  good: 'bg-good/10 text-good-ink ring-good/30',
  warning: 'bg-warning/15 text-amber-700 dark:text-warning ring-warning/40',
  serious: 'bg-serious/15 text-orange-700 dark:text-serious ring-serious/40',
  critical: 'bg-critical/10 text-critical ring-critical/30',
}
export type Tone = keyof typeof tone
export function Badge({ children, tone: t = 'neutral', className, icon }: { children: ReactNode; tone?: Tone; className?: string; icon?: ReactNode }) {
  return (
    <span className={cx('inline-flex w-fit shrink-0 items-center gap-1 rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset whitespace-nowrap [&_svg]:size-3', tone[t], className)}>
      {icon}
      {children}
    </span>
  )
}

/** Status is never color-alone: dot + icon + label. */
export function SeverityBadge({ severity }: { severity: string }) {
  const m: Record<string, [Tone, ReactNode, string]> = {
    critical: ['critical', <OctagonAlert key="c" className="size-3" />, 'Critical'],
    warning: ['warning', <AlertTriangle key="w" className="size-3" />, 'Warning'],
    info: ['brand', <Info key="i" className="size-3" />, 'Info'],
  }
  const [t, icon, label] = m[severity] ?? m.info
  return <Badge tone={t} icon={icon}>{label}</Badge>
}

export function OnlineDot({ online, className }: { online: boolean; className?: string }) {
  return (
    <span
      className={cx('inline-block size-2 shrink-0 rounded-full', online ? 'bg-good pulse-dot' : 'bg-muted/50', className)}
      aria-label={online ? 'Online' : 'Offline'}
      title={online ? 'Online' : 'Offline'}
    />
  )
}

export function StatusPill({ online }: { online: boolean }) {
  return online
    ? <Badge tone="good" icon={<OnlineDot online />}>Online</Badge>
    : <Badge tone="neutral" icon={<OnlineDot online={false} />}>Offline</Badge>
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string; icon?: ReactNode; count?: number }[]; value: T; onChange: (v: T) => void }) {
  return (
    <div role="tablist" className="inline-flex h-9 max-w-full items-center gap-0.5 overflow-x-auto rounded-lg bg-panel-2 p-0.75 text-muted">
      {tabs.map((t) => (
        <button
          key={t.id}
          role="tab"
          aria-selected={value === t.id}
          onClick={() => onChange(t.id)}
          className={cx(
            'inline-flex h-[calc(100%-1px)] items-center justify-center gap-1.5 whitespace-nowrap rounded-md border border-transparent px-2.5 text-sm font-medium transition-[color,box-shadow] [&_svg]:size-4',
            value === t.id ? 'bg-panel text-fg shadow-sm dark:border-line-strong dark:bg-white/6' : 'text-muted hover:text-fg',
          )}
        >
          {t.icon}
          {t.label}
          {t.count !== undefined && <span className="rounded bg-panel-2 px-1 text-[10px] tabular text-muted ring-1 ring-line">{t.count}</span>}
          
        </button>
      ))}
    </div>
  )
}

export function Segmented<T extends string | number>({ options, value, onChange }: { options: { value: T; label: string }[]; value: T; onChange: (v: T) => void }) {
  return (
    <div className="inline-flex h-7 items-center rounded-lg bg-panel-2 p-0.75">
      {options.map((o) => (
        <button
          key={String(o.value)}
          onClick={() => onChange(o.value)}
          className={cx('h-full rounded-md border border-transparent px-2 text-xs font-medium transition', value === o.value ? 'bg-panel text-fg shadow-sm dark:border-line-strong dark:bg-white/6' : 'text-muted hover:text-fg')}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function Modal({ open, onClose, title, children, footer, width = 'max-w-lg' }: {
  open: boolean; onClose: () => void; title: ReactNode; children: ReactNode; footer?: ReactNode; width?: string
}) {
  useEffect(() => {
    if (!open) return
    const h = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', h)
    return () => window.removeEventListener('keydown', h)
  }, [open, onClose])
  if (!open) return null
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/50 p-4 pt-[10vh]" onMouseDown={onClose}>
      <div role="dialog" aria-modal className={cx('w-full rounded-lg border border-line bg-panel shadow-lg animate-rise', width)} onMouseDown={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 pt-4 pb-1">
          <h3 className="text-base leading-none font-semibold">{title}</h3>
          <button onClick={onClose} className="rounded-sm p-1 text-muted opacity-70 transition hover:opacity-100 hover:text-fg" aria-label="Close"><X className="size-4" /></button>
        </div>
        <div className="max-h-[75vh] overflow-y-auto px-5 py-3">{children}</div>
        {footer && <div className="flex justify-end gap-2 px-5 pb-4 pt-1">{footer}</div>}
      </div>
    </div>,
    document.body,
  )
}

export function PageHeader({ title, subtitle, actions, icon }: { title: ReactNode; subtitle?: ReactNode; actions?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div className="flex items-center gap-3 min-w-0">
        {icon && <div className="grid size-10 place-items-center rounded-lg border border-line bg-panel-2 text-fg [&_svg]:size-5">{icon}</div>}
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
          {subtitle && <p className="mt-0.5 text-sm text-muted">{subtitle}</p>}
        </div>
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

export function Stat({ label, value, sub, icon, trend }: { label: string; value: ReactNode; sub?: ReactNode; icon?: ReactNode; trend?: { value: number; goodWhenDown?: boolean } }) {
  return (
    <Card className="relative overflow-hidden">
      <div className="flex items-start justify-between gap-2">
        <p className="text-xs text-muted">{label}</p>
        {icon && <span className="text-muted [&_svg]:size-3.5">{icon}</span>}
      </div>
      <p className="mt-1 text-xl font-semibold tracking-tight tabular xl:text-2xl">{value}</p>
      <div className="mt-1 flex items-center gap-2 text-xs text-muted">
        {trend && Number.isFinite(trend.value) && (
          <span className={cx('font-medium tabular', (trend.value <= 0) === !!trend.goodWhenDown || trend.value === 0 ? 'text-good-ink' : 'text-fg-2')}>
            {trend.value > 0 ? '▲' : trend.value < 0 ? '▼' : '•'} {Math.abs(trend.value).toFixed(0)}%
          </span>
        )}
        {sub}
      </div>
    </Card>
  )
}

export function Empty({ icon, title, children }: { icon?: ReactNode; title: string; children?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-1.5 py-6 text-center">
      {icon && <div className="grid size-8 place-items-center rounded-md border border-line bg-panel-2 text-fg [&_svg]:size-4">{icon}</div>}
      <p className="text-sm font-medium">{title}</p>
      {children && <div className="max-w-sm text-xs text-muted">{children}</div>}
    </div>
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={cx('skeleton rounded-lg', className)} />
}

export function Meter({ value, label, warnAt = 80, critAt = 92 }: { value: number | null | undefined; label?: string; warnAt?: number; critAt?: number }) {
  const v = Math.max(0, Math.min(100, value ?? 0))
  const color = v >= critAt ? 'var(--critical)' : v >= warnAt ? 'var(--warning)' : 'var(--series-1)'
  return (
    <div className="w-full">
      {label && <div className="mb-1 flex justify-between text-[11px] text-muted"><span>{label}</span><span className="tabular text-fg-2">{value === null || value === undefined ? '—' : `${Math.round(v)}%`}</span></div>}
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-panel-2" role="meter" aria-valuenow={v} aria-valuemin={0} aria-valuemax={100}>
        <div className="h-full rounded-full transition-[width] duration-700" style={{ width: `${v}%`, background: color }} />
      </div>
    </div>
  )
}

/** Radial gauge for the live summary. */
export function Gauge({ value, label, sub }: { value: number | null | undefined; label: string; sub?: string }) {
  const v = Math.max(0, Math.min(100, value ?? 0))
  const r = 38, c = 2 * Math.PI * r, arc = c * 0.75
  const color = v >= 92 ? 'var(--critical)' : v >= 80 ? 'var(--warning)' : 'var(--series-1)'
  return (
    <div className="flex flex-col items-center">
      <svg viewBox="0 0 100 100" className="size-28 rotate-[-225deg]" aria-label={`${label} ${Math.round(v)}%`}>
        <circle cx="50" cy="50" r={r} fill="none" stroke="var(--panel-2)" strokeWidth="8" strokeDasharray={`${arc} ${c}`} strokeLinecap="round" />
        <circle cx="50" cy="50" r={r} fill="none" stroke={color} strokeWidth="8" strokeDasharray={`${(arc * v) / 100} ${c}`} strokeLinecap="round"
          style={{ transition: 'stroke-dasharray .8s ease' }} />
      </svg>
      <div className="-mt-18.5 mb-8 text-center">
        <div className="text-2xl font-semibold tabular">{value === null || value === undefined ? '—' : Math.round(v)}<span className="text-sm text-muted">%</span></div>
      </div>
      <div className="text-xs font-medium text-fg-2">{label}</div>
      {sub && <div className="text-[11px] text-muted">{sub}</div>}
    </div>
  )
}

export function Callout({ tone: t = 'brand', children, icon }: { tone?: 'brand' | 'warning' | 'critical' | 'good'; children: ReactNode; icon?: ReactNode }) {
  const styles = {
    brand: 'border-brand/25 bg-brand-soft',
    warning: 'border-warning/40 bg-warning/10',
    critical: 'border-critical/40 bg-panel text-critical [&_div]:text-fg-2',
    good: 'border-good/30 bg-good/8',
  }
  const icons = { brand: <Info className="size-4 shrink-0 translate-y-0.5 text-brand-ink" />, warning: <AlertTriangle className="size-4 text-amber-600 dark:text-warning" />, critical: <OctagonAlert className="size-4 text-critical" />, good: <CheckCircle2 className="size-4 text-good-ink" /> }
  return <div className={cx('flex gap-2.5 rounded-md border px-3 py-2 text-[13px]', styles[t])}>{icon ?? icons[t]}<div className="min-w-0 flex-1">{children}</div></div>
}

export function Kv({ items }: { items: [string, ReactNode][] }) {
  return (
    <dl className="divide-y divide-line text-[13px]">
      {items.map(([k, v]) => (
        <div key={k} className="flex items-center justify-between gap-3 py-1.5">
          <dt className="shrink-0 text-muted">{k}</dt>
          <dd className="min-w-0 truncate text-right text-fg">{v ?? '—'}</dd>
        </div>
      ))}
    </dl>
  )
}

export function Table({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={cx('overflow-x-auto', className)}>
      <table className="w-full caption-bottom text-sm [&_thead_tr]:border-b [&_thead_tr]:border-line [&_th]:h-8 [&_th]:whitespace-nowrap [&_th]:px-2.5 [&_th]:text-left [&_th]:align-middle [&_th]:text-xs [&_th]:font-medium [&_th]:text-muted [&_td]:px-2.5 [&_td]:py-1.5 [&_td]:align-middle [&_tbody_tr]:border-b [&_tbody_tr]:border-line [&_tbody_tr:last-child]:border-0 [&_tbody_tr]:transition-colors [&_tbody_tr:hover]:bg-panel-2/50">
        {children}
      </table>
    </div>
  )
}
