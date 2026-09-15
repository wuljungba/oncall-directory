import { useState } from 'react'
import { AlertTriangle, CheckCircle, X } from 'lucide-react'
import {
  readAdminConsentResult,
  clearAdminConsentResult,
  type AdminConsentResult,
} from '@/utils/adminConsent'

/**
 * Tells a customer's Entra admin what happened after they granted consent.
 *
 * They arrive from Microsoft on a redirect to /admin, with no OnCall session, so the app
 * routes them to the sign-in page — previously with no sign that the consent they were asked
 * to grant had registered. Whether it worked decides whether their whole staff can sign in,
 * so it is worth saying plainly.
 *
 * Renders nothing unless this browser actually came back from a consent redirect.
 */
export default function AdminConsentBanner() {
  const [result, setResult] = useState<AdminConsentResult | null>(() => readAdminConsentResult())

  if (!result) return null

  function dismiss() {
    clearAdminConsentResult()
    setResult(null)
  }

  const ok = result.ok
  return (
    <div
      role="status"
      className={`mb-6 rounded-xl border px-4 py-3 text-sm ${
        ok
          ? 'border-green-600/30 bg-green-600/10 text-green-300'
          : 'border-red-600/30 bg-red-600/10 text-red-300'
      }`}
    >
      <div className="flex items-start gap-3">
        {ok
          ? <CheckCircle className="w-5 h-5 shrink-0 text-green-500" />
          : <AlertTriangle className="w-5 h-5 shrink-0 text-red-500" />}
        <div className="flex-1 min-w-0">
          {ok ? (
            <>
              <p className="font-medium">Consent granted for your organisation.</p>
              <p className="text-gray-400 mt-1">
                Your staff can sign in to OnCall now. You do not need an account here
                yourself.
              </p>
            </>
          ) : (
            <>
              <p className="font-medium">Consent was not granted.</p>
              <p className="text-gray-400 mt-1">
                Nothing changed for your organisation. Open the link again, or ask whoever
                sent it for a new one.
              </p>
              {(result.error || result.description) && (
                <p className="text-xs text-gray-500 mt-1.5 break-words font-mono">
                  {[result.error, result.description].filter(Boolean).join(': ')}
                </p>
              )}
            </>
          )}
          {result.tenantId && (
            <p className="text-xs text-gray-600 mt-1.5 font-mono break-all">
              directory {result.tenantId}
            </p>
          )}
        </div>
        <button
          type="button"
          onClick={dismiss}
          aria-label="Dismiss"
          className="p-1 -m-1 text-gray-500 hover:text-gray-300 shrink-0"
        >
          <X className="w-4 h-4" />
        </button>
      </div>
    </div>
  )
}
