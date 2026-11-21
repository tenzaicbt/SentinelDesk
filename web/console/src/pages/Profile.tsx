import { useState } from 'react'
import { QRCodeSVG } from 'qrcode.react'
import { toast } from 'sonner'
import { KeyRound, ShieldCheck, Smartphone, UserCog } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { Badge, Button, Callout, Card, Field, Input, Kv, PageHeader } from '../components/ui'
import { Brand } from '../components/Layout'
import { promptDialog } from '../components/dialog'

function PasswordForm({ onDone }: { onDone?: () => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (next !== confirm) return setError('Passwords do not match')
    setBusy(true); setError(null)
    try {
      await api.post('/api/auth/change-password', { current, next })
      toast.success('Password updated')
      setCurrent(''); setNext(''); setConfirm('')
      onDone?.()
    } catch (err) { setError(err instanceof ApiError ? err.message : 'Failed') }
    finally { setBusy(false) }
  }
  return (
    <form onSubmit={submit} className="space-y-3">
      {error && <Callout tone="critical">{error}</Callout>}
      <Field label="Current password"><Input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} required autoComplete="current-password" /></Field>
      <Field label="New password" hint="At least 10 characters with upper, lower case and a digit">
        <Input type="password" value={next} onChange={(e) => setNext(e.target.value)} required autoComplete="new-password" />
      </Field>
      <Field label="Confirm new password"><Input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required autoComplete="new-password" /></Field>
      <Button variant="primary" loading={busy} icon={<KeyRound className="size-4" />}>Update password</Button>
    </form>
  )
}

export function ChangePasswordGate() {
  const { refresh, logout } = useAuth()
  return (
    <div className="grid min-h-full place-items-center p-4">
      <div className="w-full max-w-sm animate-rise">
        <Brand />
        <h2 className="mt-8 text-xl font-semibold">Set a new password</h2>
        <p className="mb-4 mt-1 text-sm text-muted">Your password was issued by an administrator and must be changed before continuing.</p>
        <PasswordForm onDone={refresh} />
        <button onClick={logout} className="mt-4 text-xs text-muted hover:text-fg">Sign out</button>
      </div>
    </div>
  )
}

export default function Profile() {
  const { me, refresh } = useAuth()
  const [setup, setSetup] = useState<{ secret: string; uri: string } | null>(null)
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)

  const start = async () => setSetup(await api.post<{ secret: string; uri: string }>('/api/auth/mfa/setup'))
  const enable = async () => {
    setBusy(true)
    try { await api.post('/api/auth/mfa/enable', { code }); toast.success('Two-step verification enabled'); setSetup(null); setCode(''); refresh() }
    catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
    finally { setBusy(false) }
  }
  const disable = async () => {
    const c = await promptDialog({
      title: 'Disable two-step verification', message: 'Enter a current code from your authenticator app to confirm.',
      label: 'Authenticator code', placeholder: '123456', inputMode: 'numeric', autoComplete: 'one-time-code', maxLength: 6,
      confirmLabel: 'Disable', validate: (v) => (/^\d{6}$/.test(v) ? undefined : 'Enter the 6-digit code'),
    })
    if (!c) return
    try { await api.post('/api/auth/mfa/disable', { code: c }); toast.success('Two-step verification disabled'); refresh() }
    catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }

  return (
    <>
      <PageHeader title="Profile & security" icon={<UserCog className="size-5" />} subtitle="Manage your sign-in credentials" />
      <div className="grid gap-3 lg:grid-cols-2">
        <Card title="Account">
          <Kv items={[
            ['Username', me?.username], ['Name', me?.displayName], ['Role', <Badge tone="brand">{me?.role}</Badge>],
            ['Scope', me?.scopeName ?? 'Entire organization'],
          ]} />
        </Card>
        <Card title="Two-step verification" subtitle="TOTP authenticator (Microsoft / Google Authenticator)"
          actions={me?.mfaEnabled ? <Badge tone="good" icon={<ShieldCheck className="size-3" />}>Enabled</Badge> : <Badge tone="warning">Not enabled</Badge>}>
          {me?.mfaEnabled ? (
            <div className="space-y-3">
              <p className="text-sm text-fg-2">Your account requires a verification code at every sign-in.</p>
              <Button variant="secondary" onClick={disable}>Disable</Button>
            </div>
          ) : setup ? (
            <div className="flex flex-col gap-3 sm:flex-row">
              <div className="rounded-xl bg-white p-3 self-start"><QRCodeSVG value={setup.uri} size={148} /></div>
              <div className="flex-1 space-y-3">
                <p className="text-sm text-fg-2">Scan the QR code, or enter this key manually:</p>
                <code className="block break-all rounded-lg bg-panel-2 px-3 py-2 font-mono text-xs">{setup.secret}</code>
                <Field label="6-digit code"><Input value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" maxLength={7} className="font-mono tracking-widest" /></Field>
                <Button variant="primary" onClick={enable} loading={busy}>Verify & enable</Button>
              </div>
            </div>
          ) : (
            <div className="space-y-3">
              <Callout tone="warning">Administrators should protect console access with a second factor.</Callout>
              <Button variant="primary" onClick={start} icon={<Smartphone className="size-4" />}>Set up authenticator</Button>
            </div>
          )}
        </Card>
        <Card title="Change password"><PasswordForm /></Card>
      </div>
    </>
  )
}
