import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { MsalProvider } from '@azure/msal-react'
import { msalInstance } from '@/services/auth'
import App from './App'
import ErrorBoundary from './components/ErrorBoundary'
import './index.css'

// Global unhandled promise rejection handler
window.addEventListener('unhandledrejection', (event) => {
  console.error('[Global] Unhandled promise rejection:', event.reason)
  // Prevent the default console noise
  event.preventDefault()
})

// Global error handler for render-time errors the ErrorBoundary misses
window.addEventListener('error', (event) => {
  console.error('[Global] Uncaught error:', event.error || event.message)
})

msalInstance.initialize().then(() => {
  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <MsalProvider instance={msalInstance}>
        <BrowserRouter>
          <ErrorBoundary>
            <App />
          </ErrorBoundary>
        </BrowserRouter>
      </MsalProvider>
    </React.StrictMode>
  )
})
