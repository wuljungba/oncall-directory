import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { MsalProvider } from '@azure/msal-react'
import { GoogleOAuthProvider } from '@react-oauth/google'
import { MicrosoftAuthProvider } from '@/services/auth'
import App from './App'
import ErrorBoundary from './components/ErrorBoundary'
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

function renderApp(msalInstance?: ReturnType<MicrosoftAuthProvider['getMsalInstance']>) {
  const appContent = (
    <BrowserRouter>
      <ErrorBoundary>
        <App />
      </ErrorBoundary>
    </BrowserRouter>
  )

  // GoogleOAuthProvider must wrap the app for google.accounts.* to be available.
  const withGoogle = GOOGLE_ENABLED ? (
    <GoogleOAuthProvider clientId={GOOGLE_CLIENT_ID}>{appContent}</GoogleOAuthProvider>
  ) : (
    appContent
  )

  const app = msalInstance ? (
    <React.StrictMode>
      <MsalProvider instance={msalInstance}>{withGoogle}</MsalProvider>
    </React.StrictMode>
  ) : (
    <React.StrictMode>{withGoogle}</React.StrictMode>
  )

  ReactDOM.createRoot(document.getElementById('root')!).render(app)
}

if (DEV_AUTH) {
  // Dev mode: skip MSAL initialization entirely — no Entra ID needed
  renderApp()
} else {
  // Initialize MSAL for Microsoft Entra ID auth
  const msalProvider = new MicrosoftAuthProvider()
  const msalInstance = msalProvider.getMsalInstance()

  msalInstance.initialize().then(() => {
    renderApp(msalInstance)
  }).catch((err) => {
    console.error('[MSAL] Initialization failed, falling back without Entra ID:', err)
    renderApp()
  })
}
