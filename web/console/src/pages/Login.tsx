import { useState } from 'react'
import { KeyRound, Lock, Radio, ShieldCheck, Sparkles } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { Button, Callout, Field, Input } from '../components/ui'
import { Brand, Credit } from '../components/Layout'
import brandIcon from '../assets/brand/icon.png'

interface LoginResult { mfaRequired: boolean; token: string }

export function Login() {
  const { login } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [mfaToken, setMfaToken] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      if (mfaToken) {
        const r = await api.post<LoginResult>('/api/auth/mfa', { code }, mfaToken)
        await login(r.token)
      } else {
        const r = await api.post<LoginResult>('/api/auth/login', { username, password })
        if (r.mfaRequired) setMfaToken(r.token)
        else await login(r.token)
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unable to reach the server')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="grid min-h-full lg:grid-cols-[1.1fr_1fr]">
      <div className="relative hidden overflow-hidden bg-[#0a0a0a] p-12 text-white lg:flex lg:flex-col">
        <div className="pointer-events-none absolute -left-32 -top-32 size-130 rounded-full bg-[#ff6a00]/22 blur-3xl" />
        <div className="pointer-events-none absolute -bottom-40 right-0 size-120 rounded-full bg-[#ff6a00]/10 blur-3xl" />
        <img src={brandIcon} alt="" className="pointer-events-none absolute -right-24 top-1/2 size-110 -translate-y-1/2 opacity-[0.08]" />
        <div className="relative"><Brand inverse size="lg" /></div>
        <div className="relative mt-auto max-w-lg">
          <h1 className="text-4xl font-semibold leading-tight tracking-tight">
            Every company device,<br /><span className="text-[#ff6a00]">one secure view.</span>
          </h1>
          <p className="mt-4 text-white/70">
            Island-wide visibility of health, inventory, applications and data usage — with tamper-protected agents and end-to-end encryption.
          </p>
          <ul className="mt-8 space-y-3 text-sm text-white/80">
            {[
              [Radio, 'Real-time presence and live bandwidth for every branch'],
              [ShieldCheck, 'Mutual TLS, AES-256-GCM payloads, signed policies & commands'],
              [Lock, 'Role-based access, MFA and an immutable audit trail'],
              [Sparkles, 'Privacy controls built in — PDPA No. 9 of 2022 aligned'],
            ].map(([Icon, t], i) => {
              const I = Icon as typeof Radio
              return <li key={i} className="flex items-center gap-3"><span className="grid size-7 place-items-center rounded-lg bg-[#ff6a00]/15 text-[#ff8a3d]"><I className="size-4" /></span>{t as string}</li>
            })}
          </ul>
        </div>
      </div>

      <div className="flex items-center justify-center p-6">
        <form onSubmit={submit} className="w-full max-w-sm animate-rise">
          <div className="mb-8 lg:hidden"><Brand /></div>
          <h2 className="text-2xl font-semibold tracking-tight">{mfaToken ? 'Two-step verification' : 'Sign in to the console'}</h2>
          <p className="mt-1.5 text-sm text-muted">
            {mfaToken ? 'Enter the 6-digit code from your authenticator app.' : 'Authorised IT personnel only. All access is audited.'}
          </p>
          <div className="mt-7 space-y-4">
            {error && <Callout tone="critical">{error}</Callout>}
            {!mfaToken ? (
              <>
                <Field label="Username"><Input autoFocus autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required /></Field>
                <Field label="Password"><Input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required /></Field>
              </>
            ) : (
              <Field label="Verification code">
                <Input autoFocus inputMode="numeric" autoComplete="one-time-code" maxLength={7} value={code}
                  onChange={(e) => setCode(e.target.value)} className="h-12 text-center font-mono text-xl tracking-[0.5em]" placeholder="000000" />
              </Field>
            )}
            <Button variant="primary" className="h-10 w-full" loading={busy} icon={<KeyRound className="size-4" />}>
              {mfaToken ? 'Verify' : 'Sign in'}
            </Button>
            {mfaToken && <button type="button" className="w-full text-center text-xs text-muted hover:text-fg" onClick={() => { setMfaToken(null); setCode('') }}>Back</button>}
          </div>
          <p className="mt-10 text-center text-[11px] text-muted">Protected system · Unauthorised access is prohibited</p>
          <Credit className="mt-1 text-center" />
        </form>
      </div>
    </div>
  )
}
