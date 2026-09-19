import { useState } from 'react'
import { AlertTriangle, Check, Copy, X } from 'lucide-react'
import type { OnboardingInvite } from '@/types'

/**
 * The two links that connect a customer's directory, handed over for sending.
 *
 * Nothing here can grant the consent: it happens inside the customer's own directory, by one
 * of their administrators. So the whole job is producing the exact links and making it hard
 * to send the wrong one — which is why they are copied separately, each with the sentence
 * explaining what it does, rather than as one block to be picked apart by hand.
 *
 * Both are needed, and they fail differently. Without the sign-in link every one of their
 * staff stops at "Need admin approval"; without the directory link they can sign in to an
 * empty directory that never syncs.
 */
export default function OnboardingInviteModal({ tenantName, invite, onClose }: {
  tenantName: string
  invite: OnboardingInvite
  onClose: () => void
}) {
  const [copied, setCopied] = useState<string | null>(null)
  const [copyFailed, setCopyFailed] = useState(false)

  async function copy(which: 'signIn' | 'directory', url: string) {
    try {
      await navigator.clipboard.writeText(url)
      setCopyFailed(false)
      setCopied(which)
      setTimeout(() => setCopied(null), 2500)
    } catch {
      // Never silent: someone who thinks they copied a link will paste whatever was on the
      // clipboard before into an email to a customer.
      setCopyFailed(true)
    }
  }

  const expires = new Date(invite.expiresAt)

  const link = (
    which: 'signIn' | 'directory',
    title: string,
    consequence: string,
    url: string,
  ) => (
    <div className="bg-gray-800/50 border border-gray-800 rounded-lg p-4 space-y-2">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-medium">{title}</p>
          <p className="text-xs text-gray-500 mt-0.5">{consequence}</p>
        </div>
        <button
          type="button"
          onClick={() => copy(which, url)}
          className="flex items-center gap-1.5 shrink-0 text-xs px-2.5 py-1.5 rounded bg-gray-800 hover:bg-gray-700 text-gray-300 transition-colors"
        >
          {copied === which
            ? <><Check className="w-3.5 h-3.5 text-green-500" /> Copied</>
            : <><Copy className="w-3.5 h-3.5" /> Copy</>}
        </button>
      </div>
      {/* Shown as well as copied, so it can be read back or copied by hand where the
          clipboard is unavailable. */}
      <p className="text-[11px] font-mono text-gray-600 break-all leading-relaxed">{url}</p>
    </div>
  )

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-xl mx-4 max-h-[90vh] overflow-y-auto"
        onClick={e => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Connect a directory to {tenantName}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>

        <div className="p-5 space-y-4">
          <p className="text-sm text-gray-400 leading-relaxed">
            Send both links to an administrator of the customer's Entra directory. Whichever
            directory they sign in to is the one that gets connected — nobody has to find or
            type a tenant ID, and a wrong one can no longer be saved.
          </p>

          {copyFailed && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 shrink-0" />
              Could not reach the clipboard. Copy the links below by hand.
            </div>
          )}

          {link(
            'signIn',
            '1. Sign-in consent',
            'Lets their staff sign in to OnCall at all. Without it everyone stops at "Need admin approval".',
            invite.signInConsentUrl,
          )}
          {link(
            'directory',
            '2. Directory access',
            'Lets OnCall read their directory, so their people and departments appear.',
            invite.directoryConsentUrl,
          )}

          <div className="text-xs text-gray-500 border-t border-gray-800 pt-4 space-y-1.5">
            <p>
              <span className="text-amber-500/90">Single use.</span> Both links carry the same
              invitation, and it is spent once the directory connects. Issue a new one if it
              expires or you need to connect a different directory.
            </p>
            <p>
              Expires {expires.toLocaleDateString()} at {expires.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.
            </p>
          </div>

          <div className="flex justify-end pt-1">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors"
            >
              Done
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
