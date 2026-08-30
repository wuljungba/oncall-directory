import { useState, useEffect } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import type { AuthUser, AuthProviderType } from '@/services/auth'

interface LoginPageProps {
  isLoading: boolean
  isAuthenticated: boolean
  user: AuthUser | null
  signIn: (provider?: AuthProviderType, email?: string, password?: string) => Promise<void>
}

export default function LoginPage({
  isLoading,
  isAuthenticated,
  signIn,
}: LoginPageProps) {
  const navigate = useNavigate()
  const [selectedProvider, setSelectedProvider] = useState<AuthProviderType | null>(null)

  useEffect(() => {
    if (isAuthenticated) navigate('/dashboard', { replace: true })
  }, [isAuthenticated, navigate])

  async function handleSsoSignIn(provider: AuthProviderType) {
    setSelectedProvider(provider)
    await signIn(provider)
  }

  if (isLoading) {
    return (
      <div className="min-h-screen bg-gray-950 flex items-center justify-center">
        <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-amber-600" />
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-gray-950 flex items-center justify-center p-4">
      <div className="w-full max-w-md">
        <div className="text-center mb-8">
          <div className="inline-flex items-center justify-center w-16 h-16 rounded-2xl bg-amber-600/10 mb-4">
            <svg
              className="w-8 h-8 text-amber-500"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={2}
                d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z"
              />
            </svg>
          </div>
          <h1 className="text-2xl font-bold text-gray-100">OnCall Schedule</h1>
          <p className="text-gray-500 mt-2">
            Sign in to manage on-call schedules and your phone directory
          </p>
        </div>

        <div className="space-y-3">
            {/* Microsoft */}
            <button
              onClick={() => handleSsoSignIn('microsoft')}
              disabled={isLoading}
              className="w-full flex items-center justify-center gap-3 px-6 py-3 bg-gray-900 border border-gray-700 rounded-xl text-gray-100 hover:bg-gray-800 transition-colors disabled:opacity-50"
            >
              <svg className="w-5 h-5 shrink-0" viewBox="0 0 21 21" fill="none">
                <rect x="1" y="1" width="9" height="9" fill="#f25022" />
                <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
                <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
                <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
              </svg>
              {selectedProvider === 'microsoft' && isLoading ? 'Signing in...' : 'Sign in with Microsoft'}
            </button>

            {/* Divider */}
            <div className="flex items-center gap-3 py-1">
              <div className="flex-1 h-px bg-gray-800" />
              <span className="text-xs text-gray-600">or</span>
              <div className="flex-1 h-px bg-gray-800" />
            </div>

            {/* Google */}
            <button
              onClick={() => handleSsoSignIn('google')}
              disabled={isLoading}
              className="w-full flex items-center justify-center gap-3 px-6 py-3 bg-white hover:bg-gray-100 rounded-xl text-gray-900 font-medium transition-colors disabled:opacity-50 border border-gray-200"
            >
              <svg className="w-5 h-5 shrink-0" viewBox="0 0 24 24" fill="none">
                <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z" fill="#4285F4" />
                <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853" />
                <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05" />
                <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335" />
              </svg>
              {selectedProvider === 'google' && isLoading ? 'Signing in...' : 'Sign in with Google'}
            </button>
          </div>

        {/* Signing in proves who you are and grants nothing until an admin scopes you
            to a tenant, so someone new could sign in successfully and land nowhere.
            This is the way to ask. */}
        <p className="text-center text-sm text-gray-500 mt-8">
          New here?{' '}
          <Link to="/request-access" className="text-amber-500 hover:text-amber-400 font-medium">
            Request access
          </Link>
        </p>

        <p className="text-center text-xs text-gray-600 mt-6">
          Healthcare-grade on-call scheduling &amp; phone directory
        </p>
      </div>
    </div>
  )
}
