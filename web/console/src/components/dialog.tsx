import { useEffect, useState, type ReactNode } from 'react'
import { AlertTriangle, HelpCircle, Trash2 } from 'lucide-react'
import { Button, Callout, cx, Field, Input, Modal } from './ui'

/**
 * In-app replacements for window.confirm / window.prompt, styled like the rest of the console.
 *   if (!(await confirmDialog({ title: 'Delete policy?', tone: 'danger' }))) return
 *   const name = await promptDialog({ title: 'New policy', label: 'Policy name' })
 * Render <DialogHost /> once near the app root.
 */
export interface ConfirmOptions {
  title: ReactNode
  message?: ReactNode
  /** Highlighted note under the message (e.g. "device is offline, runs on next check-in"). */
  note?: ReactNode
  noteTone?: 'brand' | 'warning' | 'critical' | 'good'
  confirmLabel?: string
  cancelLabel?: string
  /** danger = destructive action: red button + warning icon */
  tone?: 'default' | 'danger'
}

export interface PromptOptions {
  title: ReactNode
  message?: ReactNode
  label: string
  defaultValue?: string
  placeholder?: string
  confirmLabel?: string
  cancelLabel?: string
  inputMode?: 'text' | 'numeric'
  autoComplete?: string
  maxLength?: number
  /** Return an error message to block submit, or nothing when valid. */
  validate?: (value: string) => string | undefined
}

type Request =
  | { kind: 'confirm'; opts: ConfirmOptions; resolve: (v: boolean) => void }
  | { kind: 'prompt'; opts: PromptOptions; resolve: (v: string | null) => void }

let push: ((r: Request) => void) | null = null
const pending: Request[] = []

function enqueue(r: Request) {
  if (push) push(r)
  else pending.push(r)
}

export function confirmDialog(opts: ConfirmOptions): Promise<boolean> {
  return new Promise((resolve) => enqueue({ kind: 'confirm', opts, resolve }))
}

export function promptDialog(opts: PromptOptions): Promise<string | null> {
  return new Promise((resolve) => enqueue({ kind: 'prompt', opts, resolve }))
}

export function DialogHost() {
  const [queue, setQueue] = useState<Request[]>([])
  useEffect(() => {
    push = (r) => setQueue((q) => [...q, r])
    if (pending.length) setQueue((q) => [...q, ...pending.splice(0)])
    return () => { push = null }
  }, [])
  const current = queue[0]
  const close = (value: boolean | string | null) => {
    if (!current) return
    if (current.kind === 'confirm') current.resolve(value === true)
    else current.resolve(typeof value === 'string' ? value : null)
    setQueue((q) => q.slice(1))
  }
  if (!current) return null
  return current.kind === 'confirm'
    ? <ConfirmView key={queue.length} opts={current.opts} onClose={close} />
    : <PromptView key={queue.length} opts={current.opts} onClose={close} />
}

function ConfirmView({ opts, onClose }: { opts: ConfirmOptions; onClose: (v: boolean) => void }) {
  const danger = opts.tone === 'danger'
  return (
    <Modal open onClose={() => onClose(false)} width="max-w-md"
      title={
        <span className="flex items-center gap-2.5">
          <span className={cx('grid size-8 shrink-0 place-items-center rounded-full', danger ? 'bg-critical/10 text-critical' : 'bg-brand-soft text-brand-ink')}>
            {danger ? <AlertTriangle className="size-4" /> : <HelpCircle className="size-4" />}
          </span>
          {opts.title}
        </span>
      }
      footer={
        <>
          <Button onClick={() => onClose(false)}>{opts.cancelLabel ?? 'Cancel'}</Button>
          <Button autoFocus variant={danger ? 'danger' : 'primary'} icon={danger ? <Trash2 className="size-4" /> : undefined} onClick={() => onClose(true)}>
            {opts.confirmLabel ?? (danger ? 'Delete' : 'Confirm')}
          </Button>
        </>
      }>
      <div className="space-y-3 text-sm">
        {opts.message && <div className="text-fg-2">{opts.message}</div>}
        {opts.note && <Callout tone={opts.noteTone ?? 'warning'}>{opts.note}</Callout>}
      </div>
    </Modal>
  )
}

function PromptView({ opts, onClose }: { opts: PromptOptions; onClose: (v: string | null) => void }) {
  const [value, setValue] = useState(opts.defaultValue ?? '')
  const [error, setError] = useState<string>()
  const submit = () => {
    const v = value.trim()
    if (!v) { setError(`${opts.label} is required`); return }
    const e = opts.validate?.(v)
    if (e) { setError(e); return }
    onClose(v)
  }
  return (
    <Modal open onClose={() => onClose(null)} width="max-w-md" title={opts.title}
      footer={
        <>
          <Button onClick={() => onClose(null)}>{opts.cancelLabel ?? 'Cancel'}</Button>
          <Button variant="primary" onClick={submit}>{opts.confirmLabel ?? 'OK'}</Button>
        </>
      }>
      <form className="space-y-3" onSubmit={(e) => { e.preventDefault(); submit() }}>
        {opts.message && <p className="text-sm text-fg-2">{opts.message}</p>}
        <Field label={opts.label} hint={error && <span className="text-critical">{error}</span>}>
          <Input autoFocus value={value} placeholder={opts.placeholder} inputMode={opts.inputMode} autoComplete={opts.autoComplete}
            maxLength={opts.maxLength} onFocus={(e) => e.currentTarget.select()}
            onChange={(e) => { setValue(e.target.value); setError(undefined) }} />
        </Field>
      </form>
    </Modal>
  )
}
