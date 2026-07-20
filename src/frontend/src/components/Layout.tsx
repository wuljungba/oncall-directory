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
  LogOut,
  Menu,
  X,
} from 'lucide-react'
import { useState } from 'react'
import { useAuth } from '@/hooks/useAuth'

const navItems = [
  { path: '/', label: 'Dashboard', icon: Clock },
  { path: '/schedule', label: 'On-Call Schedule', icon: Calendar },
  { path: '/directory', label: 'Phone Directory', icon: Phone },
  { path: '/phone-trees', label: 'Phone Trees', icon: PhoneCall },
  { path: '/time-off', label: 'Time Off', icon: Users },
  { path: '/compliance', label: 'Compliance', icon: ShieldCheck },
  { path: '/settings', label: 'Settings', icon: Settings },
]

export default function Layout() {
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const { user, signOut } = useAuth()
  const { isConnected } = useSignalR()

  return (
    <div className="min-h-screen bg-gray-950 text-gray-100 flex">
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
          {navItems.map((item) => (
            <NavLink
              key={item.path}
              to={item.path}
              end={item.path === '/'}
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
              <p className="text-xs text-gray-500 truncate">{user?.username}</p>
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
          <Outlet />
        </main>
      </div>
    </div>
  )
}
