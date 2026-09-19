import { AlertTriangle, CheckCircle, Link2Off, Loader2 } from 'lucide-react'
import type { DirectoryStatus } from '@/types'

/**
 * What a subscription's directory connection is actually doing.
 *
 * "Directory connected" here used to mean only that somebody had typed a GUID into a form.
 * A typo there is silent — a wrong id matches nobody's token — so the row read as connected
 * while every one of that customer's staff was locked out. This asks Graph whether the
 * directory can be read right now, and says which of the two it is.
 *
 * The difference matters operationally: "no directory yet" is answered by sending an invite,
 * "connected but unreadable" by getting the second consent link opened, and neither is
 * visible from the tenant row alone.
 */
export default function DirectoryStatusLine({ status, loading, onInvite, onCopyConsent, copiedKey, tenantId }: {
  status?: DirectoryStatus
  loading: boolean
  onInvite: () => void
  onCopyConsent: (which: 'signIn' | 'directory') => void
  copiedKey: string | null
  tenantId: number
}) {
  if (loading) {
    return (
      <p className="text-xs text-gray-600 mt-1 flex items-center gap-1.5">
        <Loader2 className="w-3 h-3 animate-spin" /> Checking directory…
      </p>
    )
  }

  // The status call itself failed (offline, or a Graph outage). Saying nothing is better than
  // asserting either state, but the invite is still worth offering.
  if (!status) {
    return (
      <p className="text-xs text-gray-600 mt-1">
        Directory status unavailable.{' '}
        <button type="button" onClick={onInvite} className="text-gray-400 hover:text-amber-500 underline underline-offset-2">
          Send onboarding invite
        </button>
      </p>
    )
  }

  const synced = status.lastSyncAt ? new Date(status.lastSyncAt).toLocaleString() : null

  if (!status.directoryTenantId) {
    return (
      <p className="text-xs text-gray-600 mt-1 flex items-center gap-1.5 flex-wrap">
        <Link2Off className="w-3 h-3 text-gray-600" />
        <span>No directory connected — their staff cannot sign in.</span>
        <button
          type="button"
          onClick={onInvite}
          className="text-amber-600/90 hover:text-amber-500 underline underline-offset-2"
        >
          Send onboarding invite
        </button>
      </p>
    )
  }

  const copyLinks = (
    <>
      <button
        type="button"
        onClick={() => onCopyConsent('signIn')}
        title="Copies one link. Their Entra admin opens it so their staff can sign in (OnCall API)."
        className="text-gray-400 hover:text-amber-500 underline underline-offset-2"
      >
        {copiedKey === `${tenantId}:signIn` ? 'Sign-in link copied' : 'Copy sign-in consent link'}
      </button>
      {' · '}
      <button
        type="button"
        onClick={() => onCopyConsent('directory')}
        title="Copies one link. Their Entra admin opens it so OnCall can read their directory (OnCall Graph)."
        className="text-gray-400 hover:text-amber-500 underline underline-offset-2"
      >
        {copiedKey === `${tenantId}:directory` ? 'Directory link copied' : 'Copy directory consent link'}
      </button>
    </>
  )

  if (!status.canReadDirectory) {
    return (
      <div className="text-xs mt-1 space-y-1">
        <p className="flex items-center gap-1.5 flex-wrap text-red-400">
          <AlertTriangle className="w-3 h-3 shrink-0" />
          <span>
            {status.needsReconsent
              ? 'Connected, but OnCall cannot read this directory — consent is missing or was revoked.'
              : 'Connected, but this directory did not answer just now.'}
          </span>
        </p>
        <p className="text-gray-600 font-mono break-all">{status.directoryTenantId}</p>
        {status.detail && <p className="text-gray-600 break-words">{status.detail}</p>}
        <p className="text-gray-600">{copyLinks}</p>
      </div>
    )
  }

  return (
    <div className="text-xs mt-1 space-y-1">
      <p className="flex items-center gap-1.5 flex-wrap text-gray-600">
        <CheckCircle className="w-3 h-3 text-green-600 shrink-0" />
        <span className="text-green-600/90">Directory readable</span>
        {status.directoryDisplayName && <span className="text-gray-400">{status.directoryDisplayName}</span>}
        <span className="font-mono">{status.directoryTenantId}</span>
      </p>
      <p className="text-gray-600">
        {status.staffCount} active {status.staffCount === 1 ? 'person' : 'people'}
        {synced ? ` · last sync ${synced}${status.lastOutcome ? ` (${status.lastOutcome})` : ''}` : ' · never synced'}
        {' · '}
        {copyLinks}
      </p>
    </div>
  )
}
