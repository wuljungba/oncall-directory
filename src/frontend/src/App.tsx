import { Routes, Route, Navigate } from 'react-router-dom'
import { useAuth } from '@/hooks/useAuth'
import Layout from '@/components/Layout'
import LoginPage from '@/pages/LoginPage'
import Dashboard from '@/pages/Dashboard'
import SchedulePage from '@/pages/SchedulePage'
import DirectoryPage from '@/pages/DirectoryPage'
import PhoneTreePage from '@/pages/PhoneTreePage'
import TimeOffPage from '@/pages/TimeOffPage'
import SettingsPage from '@/pages/SettingsPage'

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const { isAuthenticated, isLoading } = useAuth()
  if (isLoading) return <div className="flex items-center justify-center h-screen"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
  if (!isAuthenticated) return <Navigate to="/login" replace />
  return <>{children}</>
}

export default function App() {
  const auth = useAuth()

  return (
    <Routes>
      <Route path="/login" element={<LoginPage {...auth} />} />
      <Route
        path="/"
        element={
          <ProtectedRoute>
            <Layout />
          </ProtectedRoute>
        }
      >
        <Route index element={<Dashboard />} />
        <Route path="schedule" element={<SchedulePage />} />
        <Route path="directory" element={<DirectoryPage />} />
        <Route path="phone-trees" element={<PhoneTreePage />} />
        <Route path="time-off" element={<TimeOffPage />} />
        <Route path="settings" element={<SettingsPage />} />
      </Route>
    </Routes>
  )
}
