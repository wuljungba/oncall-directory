import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { GoogleOAuthProvider } from '@react-oauth/google'
import App from './App'
import ErrorBoundary from './components/ErrorBoundary'
import { AuthProvider } from './hooks/useAuth'
import './index.css'

// Global unhandled promise rejection handler
window.addEventListener('unhandledrejection', (event) => {
  console.error('[Global] Unhandled promise rejection:', event.reason)
  event.preventDefault()
})

// Global error handler for render-time errors the ErrorBoundary misses
window.addEventListener('error', (event) => {
  console.error('[Global] Uncaught error:', event.error || event.message)
})

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true'

const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID || ''
// Google Identity Services loads its script via GoogleOAuthProvider. Only mount
// it when Google auth is actually configured (and not in dev auth mode).
const GOOGLE_ENABLED = !DEV_AUTH && !!GOOGLE_CLIENT_ID

// The MicrosoftAuthProvider (from the authFactory singleton) initializes MSAL
// lazily when AuthProvider bootstraps, so no eager MSAL bootstrapping is needed
// here. There is no MsalProvider wrapper: no component consumes
// @azure/msal-react context.
//
// AuthProvider is mounted once here, above every route, so the whole app shares a
// single session: one provider.init(), one /api/auth/me, one source of truth for
// isLoading and permissions. It sits inside GoogleOAuthProvider so the Google
// Identity Services script is present before any sign-in runs.
function renderApp() {
  const appContent = (
    <BrowserRouter>
      <ErrorBoundary>
        <AuthProvider>
          <App />
        </AuthProvider>
      </ErrorBoundary>
    </BrowserRouter>
  )

  // GoogleOAuthProvider must wrap the app for google.accounts.* to be available.
  const withGoogle = GOOGLE_ENABLED ? (
    <GoogleOAuthProvider clientId={GOOGLE_CLIENT_ID}>{appContent}</GoogleOAuthProvider>
  ) : (
    appContent
  )

  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>{withGoogle}</React.StrictMode>,
  )
}

renderApp()
