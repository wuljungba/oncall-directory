import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'

// ─── IN-APP DIALOGS ──────────────────────────────────────────────────────
//
// Replaces window.prompt / window.confirm. Those render the browser's own dialog —
// "app-oncall-prod.azurewebsites.net says" — which looks like a web page misbehaving
// rather than part of the application, cannot be styled or labelled, blocks the whole
// tab while it is open, and masks nothing (a password typed into window.prompt is shown
// in clear text). It is also suppressible: a browser told to block dialogs from this
// site returns null without asking, so on a code call the "who did you notify" question
// would silently answer itself as "nobody".
//
// These are ordinary React modals with the same promise-shaped API, so a call site reads
// much as it did before:
//
//     const notified = await dialog.prompt({ title: '...' })
//     if (notified === null) return          // cancelled
//
// Provided once at the app root — see DialogProvider in App.tsx.

export interface PromptOptions {
  /** Heading, e.g. "Resolve incident #4". */
  title: string
  /** Label above the field. */
  label?: string
  /** Extra context under the heading. */
  body?: string
  placeholder?: string
  defaultValue?: string
  confirmLabel?: string
  /** Blank input is refused rather than returned as an empty string. */
  required?: boolean
  /** Renders a masked field — window.prompt could not do this at all. */
  secret?: boolean
  multiline?: boolean
}

export interface ConfirmOptions {
  title: string
  body?: string
  confirmLabel?: string
  cancelLabel?: string
  /** Red confirm button, for anything that destroys or cancels a record. */
  danger?: boolean
}

interface DialogContextValue {
  /** Resolves to the entered text, or null if cancelled. */
  prompt: (options: PromptOptions) => Promise<string | null>
  /** Resolves true only if the confirm button was pressed. */
  confirm: (options: ConfirmOptions) => Promise<boolean>
}

function missingProvider(): never {
  throw new Error('useDialog() was called outside a <DialogProvider>.')
}

const DialogContext = createContext<DialogContextValue>({
  prompt: missingProvider,
  confirm: missingProvider,
})

export function useDialog() {
  return useContext(DialogContext)
}

type PendingPrompt = { kind: 'prompt'; options: PromptOptions }
type PendingConfirm = { kind: 'confirm'; options: ConfirmOptions }
type Pending = PendingPrompt | PendingConfirm

export function DialogProvider({ children }: { children: React.ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null)
  const [value, setValue] = useState('')
  // The resolver for the promise handed back to the caller. Kept in a ref so a re-render
  // while the dialog is open cannot lose it and leave the caller awaiting forever.
  const resolveRef = useRef<((result: string | null | boolean) => void) | null>(null)

  const settle = useCallback((result: string | null | boolean) => {
    const resolve = resolveRef.current
    resolveRef.current = null
    setPending(null)
    setValue('')
    resolve?.(result)
  }, [])

  const prompt = useCallback((options: PromptOptions) => {
    return new Promise<string | null>(resolve => {
      resolveRef.current = resolve as (result: string | null | boolean) => void
      setValue(options.defaultValue ?? '')
      setPending({ kind: 'prompt', options })
    })
  }, [])

  const confirm = useCallback((options: ConfirmOptions) => {
    return new Promise<boolean>(resolve => {
      resolveRef.current = resolve as (result: string | null | boolean) => void
      setValue('')
      setPending({ kind: 'confirm', options })
    })
  }, [])

  // A dialog unmounted mid-question (a route change, an error boundary) must not leave
  // its caller suspended on a promise that can never settle.
  useEffect(() => () => resolveRef.current?.(null), [])

  return (
    <DialogContext.Provider value={{ prompt, confirm }}>
      {children}
      {pending && (
        <DialogHost
          pending={pending}
          value={value}
          onChange={setValue}
          onCancel={() => settle(pending.kind === 'confirm' ? false : null)}
          onConfirm={() => settle(pending.kind === 'confirm' ? true : value)}
        />
      )}
    </DialogContext.Provider>
  )
}

function DialogHost({ pending, value, onChange, onCancel, onConfirm }: {
  pending: Pending
  value: string
  onChange: (next: string) => void
  onCancel: () => void
  onConfirm: () => void
}) {
  const fieldRef = useRef<HTMLInputElement | HTMLTextAreaElement | null>(null)
  const confirmRef = useRef<HTMLButtonElement | null>(null)

  const promptOptions = pending.kind === 'prompt' ? pending.options : null
  const confirmOptions = pending.kind === 'confirm' ? pending.options : null

  // Blank is only refused when the caller said the answer is required. An optional
  // question keeps window.prompt's behaviour of returning "" for an empty box.
  const blocked = !!promptOptions?.required && value.trim().length === 0

  useEffect(() => {
    const field = fieldRef.current
    if (field) {
      field.focus()
      if (field instanceof HTMLInputElement) field.select()
    } else {
      confirmRef.current?.focus()
    }
  }, [pending])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') { e.preventDefault(); onCancel() }
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [onCancel])

  const title = promptOptions?.title ?? confirmOptions?.title ?? ''
  const body = promptOptions?.body ?? confirmOptions?.body
  const danger = !!confirmOptions?.danger

  return (
    <div
      className="fixed inset-0 z-[60] flex items-center justify-center bg-black/60 px-4"
      onClick={onCancel}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-md max-h-[90vh] overflow-y-auto shadow-2xl"
        onClick={e => e.stopPropagation()}
      >
        <div className="px-5 py-4 border-b border-gray-800">
          <h2 className="text-base font-medium text-gray-100">{title}</h2>
          {body && <p className="text-xs text-gray-500 mt-1 leading-relaxed">{body}</p>}
        </div>

        <form
          onSubmit={e => { e.preventDefault(); if (!blocked) onConfirm() }}
          className="px-5 py-4 space-y-4"
        >
          {promptOptions && (
            <div>
              {promptOptions.label && (
                <label className="block text-xs text-gray-400 mb-1.5">{promptOptions.label}</label>
              )}
              {promptOptions.multiline ? (
                <textarea
                  ref={el => { fieldRef.current = el }}
                  rows={3}
                  value={value}
                  onChange={e => onChange(e.target.value)}
                  placeholder={promptOptions.placeholder}
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm text-gray-100 placeholder:text-gray-600 focus:outline-none focus:border-amber-600 resize-y"
                />
              ) : (
                <input
                  ref={el => { fieldRef.current = el }}
                  type={promptOptions.secret ? 'password' : 'text'}
                  value={value}
                  onChange={e => onChange(e.target.value)}
                  placeholder={promptOptions.placeholder}
                  autoComplete={promptOptions.secret ? 'new-password' : 'off'}
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm text-gray-100 placeholder:text-gray-600 focus:outline-none focus:border-amber-600"
                />
              )}
              {promptOptions.required && (
                <p className="text-[11px] text-gray-600 mt-1.5">Required.</p>
              )}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <button
              type="button"
              onClick={onCancel}
              className="px-3.5 py-2 rounded-lg text-sm text-gray-300 hover:text-white hover:bg-gray-800 transition-colors"
            >
              {confirmOptions?.cancelLabel ?? 'Cancel'}
            </button>
            <button
              ref={confirmRef}
              type="submit"
              disabled={blocked}
              className={`px-3.5 py-2 rounded-lg text-sm text-white transition-colors disabled:opacity-40 disabled:cursor-not-allowed ${
                danger ? 'bg-red-600 hover:bg-red-700' : 'bg-amber-600 hover:bg-amber-700'
              }`}
            >
              {promptOptions?.confirmLabel ?? confirmOptions?.confirmLabel ?? 'OK'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
