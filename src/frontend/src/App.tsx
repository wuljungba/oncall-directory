import { Routes, Route, Navigate } from 'react-router-dom'
import { useAuth } from '@/hooks/useAuth'
import Layout from '@/components/Layout'
import { ToastProvider } from '@/components/Toast'
import { DialogProvider } from '@/components/ui/Dialog'
import { SignalRProvider } from '@/hooks/useSignalR'
import LoginPage from '@/pages/LoginPage'
import Dashboard from '@/pages/Dashboard'
import SchedulePage from '@/pages/SchedulePage'
import DirectoryPage from '@/pages/DirectoryPage'
import PhoneTreePage from '@/pages/PhoneTreePage'
import CommandCenterPage from '@/pages/CommandCenterPage'
import TimeOffPage from '@/pages/TimeOffPage'
import SettingsPage from '@/pages/SettingsPage'
import CompliancePage from '@/pages/CompliancePage'
import EscalationPage from '@/pages/EscalationPage'
import LandingPage from '@/pages/LandingPage'
import RequestAccessPage from '@/pages/RequestAccessPage'
import PublicSchedulePage from '@/pages/PublicSchedulePage'
import AdminPage from '@/pages/AdminPage'

export function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { isAuthenticated, isLoading } = useAuth()
  if (isLoading) return <div className="flex items-center justify-center h-screen"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
  if (!isAuthenticated) return <Navigate to="/login" replace />
  return <>{children}</>
}

export function AdminRoute({ children }: { children: React.ReactNode }) {
  const { isAdmin, canAdminScoped, isLoading } = useAuth()
  if (isLoading) return <div className="flex items-center justify-center h-screen"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
  if (!isAdmin && !canAdminScoped) return <Navigate to="/dashboard" replace />
  return <>{children}</>
}

export default function App() {
  const auth = useAuth()

  return (
    <ToastProvider>
      <DialogProvider>
        <Routes>
          {/* Public routes */}
          <Route path="/" element={<LandingPage />} />
          <Route path="/login" element={<LoginPage {...auth} />} />
          {/* Public: the way in for someone with no account. Creates a request, not access. */}
          <Route path="/request-access" element={<RequestAccessPage />} />
          {/* Public on-call coverage permalink — no auth required */}
          <Route path="/on-call/:token" element={<PublicSchedulePage />} />

          {/* Protected routes */}
          <Route
            path="/dashboard"
            element={
              <ProtectedRoute>
                <SignalRProvider>
                  <Layout />
                </SignalRProvider>
              </ProtectedRoute>
            }
          >
            <Route index element={<Dashboard />} />
            <Route path="schedule" element={<SchedulePage />} />
            <Route path="directory" element={<DirectoryPage />} />
            <Route path="code-calls" element={<CommandCenterPage />} />
            <Route path="phone-trees" element={<PhoneTreePage />} />
            <Route path="time-off" element={<TimeOffPage />} />
            <Route path="settings" element={<SettingsPage />} />
            <Route path="compliance" element={<CompliancePage />} />
            <Route path="escalation" element={<EscalationPage />} />
          </Route>

          {/* Admin routes (inside layout, same SignalR context) */}
          <Route
            path="/admin"
            element={
              <ProtectedRoute>
                <AdminRoute>
                  <SignalRProvider>
                    <Layout />
                  </SignalRProvider>
                </AdminRoute>
              </ProtectedRoute>
            }
          >
            <Route index element={<AdminPage />} />
          </Route>
        </Routes>
      </DialogProvider>
    </ToastProvider>
  )
}
