import { Outlet, NavLink } from 'react-router-dom'
import { useSignalR } from '@/hooks/useSignalR'
import {
  Calendar,
  Phone,
  PhoneCall,
  Users,
  Settings,
  Clock,
  ShieldCheck,
  AlertTriangle,
  LogOut,
  Menu,
  X,
  Shield,
} from 'lucide-react'
import { useState } from 'react'
import { useAuth } from '@/hooks/useAuth'
import OnboardingWizard, { useOnboarding } from '@/components/OnboardingWizard'

const navItems = [
  { path: '/dashboard', label: 'Dashboard', icon: Clock },
  { path: '/dashboard/schedule', label: 'On-Call Schedule', icon: Calendar },
  { path: '/dashboard/directory', label: 'Phone Directory', icon: Phone },
  { path: '/dashboard/code-calls', label: 'Command Center', icon: PhoneCall },
  { path: '/dashboard/time-off', label: 'Time Off', icon: Users },
  { path: '/dashboard/compliance', label: 'Compliance', icon: ShieldCheck },
  { path: '/dashboard/settings', label: 'Settings', icon: Settings },
]

export default function Layout() {
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const { user, signOut, isAdmin, canAdminScoped, isLoading, permissions, permissionsUnavailable } = useAuth()
  const { isConnected } = useSignalR()
  const { showOnboarding, checking, dismiss } = useOnboarding()
  const visibleNavItems = (isAdmin || canAdminScoped)
    ? [...navItems, { path: '/admin', label: 'Admin', icon: Shield }]
    : navItems

  // A signed-in user with no permissions is a real and expected state: Entra and Google
  // tokens carry no app roles, so a first-time sign-in has access to nothing until an
  // admin grants it. Every page would fill with failed requests, which reads as "the app
  // is broken" rather than "you need to be granted access".
  //
  // A FAILED /api/auth/me is a different thing entirely and must not be reported as a
  // provisioning gap: it sends the user to chase an administrator over what is actually
  // a stopped server or a dropped network.
  const awaitingProvisioning = !isLoading && permissions.length === 0 && !permissionsUnavailable
  const serverUnreachable = !isLoading && permissionsUnavailable

  return (
    <div className="min-h-screen bg-gray-950 text-gray-100 flex">
      {/* Onboarding wizard for new users */}
      {!checking && showOnboarding && (
        <OnboardingWizard onComplete={dismiss} />
      )}
      {/* Sidebar */}
      <aside role="navigation" aria-label="Main navigation"
        className={`${
          sidebarOpen ? 'translate-x-0' : '-translate-x-full'
        } fixed inset-y-0 left-0 z-50 w-64 bg-gray-900 border-r border-gray-800 transition-transform lg:translate-x-0 lg:static lg:inset-auto`}
      >
        <div className="flex items-center justify-between h-16 px-6 border-b border-gray-800">
          <div>
            <h1 className="text-lg font-bold text-amber-500">OnCall</h1>
            <p className="text-xs text-gray-500">Schedule & Directory</p>
          </div>
          <button className="lg:hidden" onClick={() => setSidebarOpen(false)}>
            <X className="w-5 h-5" />
          </button>
        </div>

        <nav className="p-4 space-y-1">
          {visibleNavItems.map((item) => (
            <NavLink
              key={item.path}
              to={item.path}
              end={item.path === '/dashboard'}
              onClick={() => setSidebarOpen(false)}
              className={({ isActive }) =>
                `flex items-center gap-3 px-4 py-2.5 rounded-lg text-sm transition-colors ${
                  isActive
                    ? 'bg-amber-600/10 text-amber-500 border-l-2 border-amber-500'
                    : 'text-gray-400 hover:text-gray-200 hover:bg-gray-800'
                }`
              }
            >
              <item.icon className="w-4 h-4" />
              {item.label}
            </NavLink>
          ))}
        </nav>

        <div className="absolute bottom-0 left-0 right-0 p-4 border-t border-gray-800">
          <div className="flex items-center gap-3 mb-3">
            <div className="w-8 h-8 rounded-full bg-amber-600 flex items-center justify-center text-sm font-medium">
              {user?.name?.charAt(0) || '?'}
            </div>
            <div className="flex-1 min-w-0">
              <p className="text-sm truncate">{user?.name || 'User'}</p>
              <p className="text-xs text-gray-500 truncate">{user?.email}</p>
            </div>
          </div>
          <button
            onClick={signOut}
            className="flex items-center gap-2 w-full px-4 py-2 text-sm text-gray-400 hover:text-red-400 hover:bg-gray-800 rounded-lg transition-colors"
          >
            <LogOut className="w-4 h-4" />
            Sign Out
          </button>
        </div>
      </aside>

      {/* Overlay */}
      {sidebarOpen && (
        <div
          className="fixed inset-0 bg-black/50 z-40 lg:hidden"
          onClick={() => setSidebarOpen(false)}
        />
      )}

      {/* Main Content */}
      <div className="flex-1 flex flex-col min-h-screen">
        <header className="h-16 border-b border-gray-800 flex items-center px-6 gap-4 bg-gray-900/50 backdrop-blur-sm">
          <button className="lg:hidden" onClick={() => setSidebarOpen(true)}>
            <Menu className="w-5 h-5" />
          </button>
          <h2 className="text-sm font-medium text-gray-400">
            On-Call Schedule & Directory
          </h2>
          <div className="ml-auto flex items-center gap-2">
            <span
              className={`inline-block w-2 h-2 rounded-full ${
                isConnected ? 'bg-green-500' : 'bg-gray-600'
              }`}
              title={isConnected ? 'Connected (live updates)' : 'Offline'}
            />
            <span className="text-xs text-gray-600 hidden sm:inline">
              {isConnected ? 'Live' : 'Offline'}
            </span>
          </div>
        </header>

        <main role="main" className="flex-1 p-6 overflow-auto" aria-label="Main content">
          {serverUnreachable
            ? <ServerUnreachable />
            : awaitingProvisioning
              ? <AwaitingProvisioning email={user?.email} />
              : <Outlet />}
        </main>
      </div>
    </div>
  )
}

/**
 * Shown when a sign-in succeeded but the account holds no permissions yet.
 *
 * Real Entra and Google tokens carry no app roles, so a new user has access to nothing
 * until an administrator grants it (see docs/onboarding-standard.md §2). Saying so beats
 * rendering a dashboard of 403s.
 */
/**
 * Shown when /api/auth/me could not be reached.
 *
 * This used to render as "access pending", which blamed the administrator for what is
 * usually a stopped App Service, an expired deployment slot, or a dropped connection —
 * sending the user to ask for permissions they may already hold.
 */
function ServerUnreachable() {
  return (
    <div className="max-w-xl mx-auto mt-16 bg-gray-900 border border-gray-800 rounded-xl p-8 text-center">
      <div className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-red-600/10 mb-4">
        <AlertTriangle className="w-6 h-6 text-red-500" />
      </div>
      <h2 className="text-lg font-medium">Can&apos;t reach the server</h2>
      <p className="text-sm text-gray-400 mt-3">
        You are signed in, but the app could not load your access. This is a connection
        problem, not a permissions one — your account may be perfectly fine.
      </p>
      <p className="text-xs text-gray-600 mt-4">
        If this persists, the API may be stopped or still starting up. Ask whoever runs the
        deployment to check it before requesting any permission changes.
      </p>
      <button
        onClick={() => window.location.reload()}
        className="mt-5 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-xs transition-colors"
      >
        Try again
      </button>
    </div>
  )
}

function AwaitingProvisioning({ email }: { email?: string }) {
  return (
    <div className="max-w-xl mx-auto mt-16 bg-gray-900 border border-gray-800 rounded-xl p-8 text-center">
      <div className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-amber-600/10 mb-4">
        <Shield className="w-6 h-6 text-amber-500" />
      </div>
      <h2 className="text-lg font-medium">You&apos;re signed in — access pending</h2>
      <p className="text-sm text-gray-400 mt-3">
        Your account{email ? <> (<span className="text-gray-300">{email}</span>)</> : null} isn&apos;t
        set up with on-call access yet. An administrator needs to grant you at least
        <span className="text-gray-300"> Schedule.Read</span> and
        <span className="text-gray-300"> Directory.Read</span> from
        Admin → Users &amp; Permissions.
      </p>
      <p className="text-xs text-gray-600 mt-4">
        Nothing is wrong with your sign-in — this step is deliberate, so access to
        schedules and the directory is granted rather than assumed.
      </p>
      {/* A dropped /api/auth/me also lands here, so offer the cheap way out. */}
      <button
        onClick={() => window.location.reload()}
        className="mt-5 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-xs transition-colors"
      >
        Check again
      </button>
    </div>
  )
}
