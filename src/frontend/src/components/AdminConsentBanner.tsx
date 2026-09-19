import { useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle, Loader2, X } from 'lucide-react'
import { consentApi } from '@/services/api'
import {
  readAdminConsentResult,
  clearAdminConsentResult,
  submitAdminConsentResult,
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
 * When the redirect carries an invite token, this is also the moment the connection is
 * actually made: the outcome goes back to the server, which matches the invite and reads the
 * directory before connecting anything. So what is reported here is the server's verdict —
 * "Microsoft accepted your consent" and "OnCall can read your directory" are different
 * claims, and only the second one means their staff can work.
 *
 * Renders nothing unless this browser actually came back from a consent redirect.
 */
export default function AdminConsentBanner() {
  const [result, setResult] = useState<AdminConsentResult | null>(() => readAdminConsentResult())

  useEffect(() => {
    let cancelled = false
    submitAdminConsentResult(consentApi.complete).then(r => {
      if (!cancelled) setResult(r)
    })
    return () => { cancelled = true }
  }, [])

  if (!result) return null

  function dismiss() {
    clearAdminConsentResult()
    setResult(null)
  }

  // An invite-backed consent is not resolved until the server says so. Reporting Microsoft's
  // "success" while that is in flight would twice mislead: the directory may still be
  // unreadable, and the invite may name a subscription this consent cannot claim.
  const awaitingServer = !!result.state && result.delivery !== 'done'
  const ok = result.state ? result.connected === true : result.ok

  const tone = awaitingServer
    ? 'border-gray-700 bg-gray-800/40 text-gray-300'
    : ok
      ? 'border-green-600/30 bg-green-600/10 text-green-300'
      : 'border-red-600/30 bg-red-600/10 text-red-300'

  return (
    <div role="status" className={`mb-6 rounded-xl border px-4 py-3 text-sm ${tone}`}>
      <div className="flex items-start gap-3">
        {awaitingServer
          ? <Loader2 className="w-5 h-5 shrink-0 text-gray-400 animate-spin" />
          : ok
            ? <CheckCircle className="w-5 h-5 shrink-0 text-green-500" />
            : <AlertTriangle className="w-5 h-5 shrink-0 text-red-500" />}
        <div className="flex-1 min-w-0">
          {awaitingServer ? (
            <>
              <p className="font-medium">Finishing the connection…</p>
              <p className="text-gray-400 mt-1">
                Checking with OnCall that your directory can be read. This takes a moment.
              </p>
            </>
          ) : result.message ? (
            <>
              {/* The server's own words. It knows things this page cannot: whether the
                  invite was still good, and whether the directory actually answered. */}
              <p className="font-medium">
                {ok
                  ? `Your directory is connected${result.subscriptionName ? ` to ${result.subscriptionName}` : ''}.`
                  : 'Not connected yet.'}
              </p>
              <p className="text-gray-400 mt-1">{result.message}</p>
              {ok && result.needsReconsent && (
                <p className="text-amber-500/90 text-xs mt-1.5">
                  Your staff can sign in. One optional permission was not granted, so OnCall
                  cannot read your organisation's name and domains — harmless, and whoever
                  sent you the link can ask for it later.
                </p>
              )}
            </>
          ) : ok ? (
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
