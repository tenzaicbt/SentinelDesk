import { lazy, Suspense, type ReactNode } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import { useAuth } from './lib/auth'
import { HubProvider } from './lib/hub'
import { Layout } from './components/Layout'
import { Login } from './pages/Login'
import Profile, { ChangePasswordGate } from './pages/Profile'

const Dashboard = lazy(() => import('./pages/Dashboard'))
const Devices = lazy(() => import('./pages/Devices'))
const DeviceDetail = lazy(() => import('./pages/DeviceDetail'))
const Live = lazy(() => import('./pages/Live'))
const Alerts = lazy(() => import('./pages/Alerts'))
const Reports = lazy(() => import('./pages/Reports'))
const Policies = lazy(() => import('./pages/Policies'))
const PolicyNew = lazy(() => import('./pages/PolicyNew'))
const PolicyDetail = lazy(() => import('./pages/PolicyDetail'))
const Organization = lazy(() => import('./pages/Organization'))
const Agents = lazy(() => import('./pages/Agents'))
const Users = lazy(() => import('./pages/Users'))
const Audit = lazy(() => import('./pages/Audit'))


const Spinner = () => (
  <div className="grid h-64 place-items-center text-muted"><Loader2 className="size-5 animate-spin" /></div>
)

function Guard({ ok, children }: { ok: boolean; children: ReactNode }) {
  return ok ? <>{children}</> : <Navigate to="/" replace />
}

export function App() {
  const { me, loading, can } = useAuth()
  if (loading) return <div className="grid h-full place-items-center"><Spinner /></div>
  if (!me) return <Login />
  if (me.mustChangePassword) return <ChangePasswordGate />

  return (
    <HubProvider>
      <Suspense fallback={<Spinner />}>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<Dashboard />} />
            <Route path="live" element={<Guard ok={can.live}><Live /></Guard>} />
            <Route path="devices" element={<Devices />} />
            <Route path="devices/:id" element={<DeviceDetail />} />
            <Route path="alerts" element={<Alerts />} />
            <Route path="reports" element={<Reports />} />
            <Route path="policies" element={<Policies />} />
            <Route path="policies/new" element={<PolicyNew />} />
            <Route path="policies/:id" element={<PolicyDetail />} />
            <Route path="organization" element={<Organization />} />
            <Route path="agents" element={<Guard ok={can.super}><Agents /></Guard>} />
            <Route path="users" element={<Guard ok={can.super}><Users /></Guard>} />
            <Route path="audit" element={<Guard ok={can.audit}><Audit /></Guard>} />
            <Route path="profile" element={<Profile />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </Suspense>
    </HubProvider>
  )
}
