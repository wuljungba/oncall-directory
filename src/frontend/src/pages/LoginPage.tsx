import type { AccountInfo } from '@azure/msal-browser'
import { useEffect } from 'react'
import { useNavigate } from 'react-router-dom'

interface LoginPageProps {
  isLoading: boolean
  isAuthenticated: boolean
  user: AccountInfo | null
  signIn: () => Promise<void>
}

export default function LoginPage({
  isLoading,
  isAuthenticated,
  signIn,
}: LoginPageProps) {
  const navigate = useNavigate()

  useEffect(() => {
    if (isAuthenticated) navigate('/dashboard', { replace: true })
  }, [isAuthenticated, navigate])

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
            Sign in with your work account to continue
          </p>
        </div>

        <button
          onClick={signIn}
          className="w-full flex items-center justify-center gap-3 px-6 py-3 bg-gray-900 border border-gray-700 rounded-xl text-gray-100 hover:bg-gray-800 transition-colors"
        >
          <svg className="w-5 h-5" viewBox="0 0 21 21" fill="none">
            <rect x="1" y="1" width="9" height="9" fill="#f25022" />
            <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
            <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
            <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
          </svg>
          Sign in with Microsoft
        </button>

        <p className="text-center text-xs text-gray-600 mt-8">
          Healthcare-grade on-call scheduling & phone directory
        </p>
      </div>
    </div>
  )
}
