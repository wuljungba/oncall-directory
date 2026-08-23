import { useEffect, useRef, useState } from 'react'
import { AlertTriangle, Send, X } from 'lucide-react'
import { messagingApi } from '@/services/api'

const MAX_LENGTH = 300

interface SendSmsModalProps {
  employeeId: string
  employeeName: string
  /** Shown under the name for context, e.g. "primary — Emergency Medicine". */
  context?: string
  onClose: () => void
  /** Called after a successful send, so the caller can log it in its own activity feed. */
  onSent?: (detail: string) => void
}

/**
 * Composes a direct SMS to one provider.
 *
 * Text messages are not a secure channel, so the PHI warning is deliberately part of the
 * form rather than a tooltip. The result is always shown — an SMS that did not go anywhere
 * must never leave the operator assuming it did.
 */
export function SendSmsModal({
  employeeId,
  employeeName,
  context,
  onClose,
  onSent,
}: SendSmsModalProps) {
  const [message, setMessage] = useState('')
  const [sending, setSending] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    textareaRef.current?.focus()
  }, [])

  // Escape closes, matching the rest of the app's dialogs.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const remaining = MAX_LENGTH - message.length
  const canSend = message.trim().length > 0 && remaining >= 0 && !sending

  async function handleSend() {
    if (!canSend) return
    setSending(true)
    setResult(null)

    const res = await messagingApi.sendProviderSms(employeeId, message.trim())

    setSending(false)
    setResult({ ok: res.sent, text: res.detail })

    if (res.sent) {
      setMessage('')
      onSent?.(res.detail)
    }
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      role="dialog"
      aria-modal="true"
      aria-label={`Send a text message to ${employeeName}`}
      onClick={onClose}
    >
      <div
        className="w-full max-w-md rounded-xl bg-gray-900 border border-gray-700 shadow-xl"
        onClick={e => e.stopPropagation()}
      >
        <div className="flex items-start justify-between p-4 border-b border-gray-800">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-gray-100">Text {employeeName}</h2>
            {context && <p className="text-xs text-gray-500 mt-0.5 truncate">{context}</p>}
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-gray-800 text-gray-500 hover:text-gray-300"
            aria-label="Close"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="p-4 space-y-3">
          <div className="flex gap-2 rounded-lg bg-amber-500/10 border border-amber-500/30 p-2.5">
            <AlertTriangle className="w-4 h-4 text-amber-400 flex-shrink-0 mt-0.5" />
            <p className="text-xs text-amber-200/90">
              SMS is not a secure channel. Do not include patient names, identifiers, or any
              clinical detail — send a callback request instead.
            </p>
          </div>

          <div>
            <textarea
              ref={textareaRef}
              value={message}
              onChange={e => setMessage(e.target.value)}
              rows={4}
              maxLength={MAX_LENGTH}
              placeholder="e.g. Please call the ED charge desk on ext. 4412 when free."
              className="w-full rounded-lg bg-gray-800 border border-gray-700 p-2.5 text-sm text-gray-100 placeholder-gray-600 focus:outline-none focus:ring-1 focus:ring-amber-500"
            />
            <div className="flex justify-between items-center mt-1">
              <span className="text-xs text-gray-600">Your name is added automatically.</span>
              <span className={`text-xs ${remaining < 0 ? 'text-red-400' : 'text-gray-600'}`}>
                {remaining}
              </span>
            </div>
          </div>

          {result && (
            <div
              role="status"
              className={`rounded-lg p-2.5 text-xs ${
                result.ok
                  ? 'bg-green-500/10 border border-green-500/30 text-green-300'
                  : 'bg-red-500/10 border border-red-500/30 text-red-300'
              }`}
            >
              {result.ok ? 'Sent. ' : 'Not sent. '}
              {result.text}
            </div>
          )}
        </div>

        <div className="flex justify-end gap-2 p-4 border-t border-gray-800">
          <button
            onClick={onClose}
            className="px-3 py-1.5 rounded-lg text-sm text-gray-400 hover:bg-gray-800"
          >
            {result?.ok ? 'Done' : 'Cancel'}
          </button>
          <button
            onClick={handleSend}
            disabled={!canSend}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-sm bg-amber-600 hover:bg-amber-500 text-white disabled:opacity-40 disabled:cursor-not-allowed"
          >
            <Send className="w-3.5 h-3.5" />
            {sending ? 'Sending...' : 'Send text'}
          </button>
        </div>
      </div>
    </div>
  )
}
