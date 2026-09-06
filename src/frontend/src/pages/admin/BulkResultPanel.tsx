import { useState } from 'react'
import { Check, Copy, X } from 'lucide-react'
import type { BulkActionResult, BulkItemResult } from '@/types'

/**
 * What a bulk action actually did, per record.
 *
 * Not a toast. Anyone who has ever held a shift cannot be hard-deleted, so a batch is routinely
 * part-refused and the interesting half of the answer is which records were refused and why —
 * which is not something to flash for three seconds and discard.
 *
 * The useful move after a blocked delete is to deactivate those same people instead, so the
 * blocked set can be handed straight back to the selection.
 */
const GROUPS: { outcome: string; label: string; tone: string }[] = [
  { outcome: 'blockedByHistory', label: 'Blocked by history', tone: 'text-amber-400' },
  { outcome: 'notFound', label: 'Not found', tone: 'text-gray-400' },
  { outcome: 'skippedSelf', label: 'Skipped — your own record', tone: 'text-gray-400' },
  { outcome: 'alreadyInState', label: 'Already in that state', tone: 'text-gray-400' },
  { outcome: 'failed', label: 'Failed', tone: 'text-red-400' },
]

export default function BulkResultPanel({ result, onDismiss, onSelectIds }: {
  result: BulkActionResult
  onDismiss: () => void
  onSelectIds: (ids: string[]) => void
}) {
  const [expanded, setExpanded] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const groups = GROUPS
    .map(g => ({ ...g, items: result.results.filter(r => r.outcome === g.outcome) }))
    .filter(g => g.items.length > 0)

  const blocked = result.results.filter(r => r.outcome === 'blockedByHistory')

  const [copyFailed, setCopyFailed] = useState(false)

  async function copy(items: BulkItemResult[]) {
    const text = items.map(i => `${i.displayName ?? i.employeeId}${i.email ? ` <${i.email}>` : ''} — ${i.message ?? ''}`).join('\n')
    try {
      // Awaited and caught: the clipboard is denied on an unfocused page, without permission,
      // or over plain http. Reporting "Copied" regardless told the user their list was safely
      // on the clipboard when it was not, and left an unhandled rejection behind.
      await navigator.clipboard?.writeText(text)
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      setCopyFailed(true)
      setTimeout(() => setCopyFailed(false), 4000)
    }
  }

  return (
    <div className="bg-gray-900 border border-gray-800 rounded-xl">
      <div className="flex items-start justify-between gap-4 px-5 py-3 border-b border-gray-800">
        <div className="text-sm">
          <span className="text-green-400 font-medium">{result.succeeded} succeeded</span>
          {groups.map(g => (
            <span key={g.outcome} className="text-gray-500">
              {' · '}<span className={g.tone}>{g.items.length} {g.label.toLowerCase()}</span>
            </span>
          ))}
          {result.grantsRevoked > 0 && (
            <span className="text-gray-500">{' · '}{result.grantsRevoked} permission grants revoked</span>
          )}
          {result.signInsDisabled > 0 && (
            <span className="text-gray-500">{' · '}{result.signInsDisabled} sign-in accounts switched off</span>
          )}
        </div>
        <button onClick={onDismiss} className="text-gray-500 hover:text-gray-300" aria-label="Dismiss">
          <X className="w-4 h-4" />
        </button>
      </div>

      {(result.systemWideGrantsLeft > 0 || result.privilegedPrincipals > 0) && (
        <div className="px-5 py-2 border-b border-gray-800 text-xs text-amber-400 space-y-1">
          {result.systemWideGrantsLeft > 0 && (
            <p>
              {result.systemWideGrantsLeft} system-wide {result.systemWideGrantsLeft === 1 ? 'grant was' : 'grants were'} left
              in place — only a full administrator can revoke those.
            </p>
          )}
          {result.privilegedPrincipals > 0 && (
            <p>
              {result.privilegedPrincipals} of these {result.privilegedPrincipals === 1 ? 'person is an' : 'people are'} administrator
              {result.privilegedPrincipals === 1 ? '' : 's'}. This did not remove that — change it under Subscriptions.
            </p>
          )}
        </div>
      )}

      {groups.length > 0 && (
        <div className="divide-y divide-gray-800">
          {groups.map(g => (
            <div key={g.outcome}>
              <button
                onClick={() => setExpanded(expanded === g.outcome ? null : g.outcome)}
                className="w-full flex items-center justify-between px-5 py-2 text-xs hover:bg-gray-800/40"
              >
                <span className={g.tone}>{g.items.length} {g.label}</span>
                <span className="text-gray-600">{expanded === g.outcome ? 'Hide' : 'Show'}</span>
              </button>
              {expanded === g.outcome && (
                <ul className="px-5 pb-3 space-y-1">
                  {g.items.map(i => (
                    <li key={i.employeeId} className="text-xs text-gray-500">
                      <span className="text-gray-300">{i.displayName ?? 'A record you cannot administer'}</span>
                      {i.message ? ` — ${i.message}` : ''}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          ))}
        </div>
      )}

      {blocked.length > 0 && (
        <div className="flex items-center gap-2 px-5 py-3 border-t border-gray-800">
          {/* The blocked set is exactly the set you now want to deactivate instead. */}
          <button
            onClick={() => onSelectIds(blocked.map(b => b.employeeId))}
            className="px-3 py-1.5 bg-gray-800 hover:bg-gray-700 rounded-lg text-xs transition-colors"
          >
            Keep these {blocked.length} selected
          </button>
          <button
            onClick={() => copy(blocked)}
            className="flex items-center gap-1.5 px-3 py-1.5 bg-gray-800 hover:bg-gray-700 rounded-lg text-xs transition-colors"
          >
            {copied ? <Check className="w-3 h-3 text-green-400" /> : <Copy className="w-3 h-3" />}
            {copied ? 'Copied' : copyFailed ? 'Could not copy' : 'Copy list'}
          </button>
        </div>
      )}
    </div>
  )
}
