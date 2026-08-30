import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowLeft, CheckCircle, Calendar } from 'lucide-react'
import { accessRequestsApi } from '@/services/api'

// ─── REQUEST ACCESS ──────────────────────────────────────────────────────
//
// The app is invite-only, and that is deliberate: signing in proves who you are and
// grants nothing until an admin scopes you to a tenant. What was missing was a way to
// ask. Someone new could sign in perfectly successfully and land nowhere, with no
// indication that a human had to act next.
//
// This does not create an account and says so plainly. Overstating what it does would be
// worse than not having it — a person who believes they are signed up will not chase the
// admin who actually has to provision them.

export default function RequestAccessPage() {
  const [email, setEmail] = useState('')
  const [fullName, setFullName] = useState('')
  const [organization, setOrganization] = useState('')
  const [roleRequested, setRoleRequested] = useState('')
  const [note, setNote] = useState('')
  const [state, setState] = useState<'idle' | 'sending' | 'sent'>('idle')
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!email.trim()) return
    setState('sending')
    setError(null)
    try {
      await accessRequestsApi.submit({
        email: email.trim(),
        fullName: fullName.trim() || undefined,
        organization: organization.trim() || undefined,
        roleRequested: roleRequested.trim() || undefined,
        note: note.trim() || undefined,
      })
      setState('sent')
    } catch (err) {
      // Say what went wrong. A silent failure here means someone waits for a reply to a
      // request that was never recorded.
      setError(err instanceof Error ? err.message : 'Could not send your request. Try again.')
      setState('idle')
    }
  }

  return (
    <div className="min-h-screen bg-black text-gray-100 flex items-center justify-center px-4 py-12">
      <div className="w-full max-w-md">
        <div className="text-center mb-8">
          <div className="w-16 h-16 rounded-2xl bg-amber-600/15 flex items-center justify-center mx-auto mb-5">
            <Calendar className="w-8 h-8 text-amber-500" />
          </div>
          <h1 className="text-3xl font-bold">Request access</h1>
          <p className="text-gray-500 mt-2 text-sm leading-relaxed">
            Accounts here are set up by an administrator. Tell us who you are and we will
            pass it on.
          </p>
        </div>

        {state === 'sent' ? (
          <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6 text-center">
            <CheckCircle className="w-10 h-10 text-green-500 mx-auto mb-4" />
            <h2 className="font-medium text-lg">Request sent</h2>
            <p className="text-sm text-gray-400 mt-2 leading-relaxed">
              An administrator will review it and reply by email. This did not create an
              account — you will not be able to sign in until someone grants you access.
            </p>
            <Link
              to="/"
              className="inline-flex items-center gap-2 mt-6 text-sm text-amber-500 hover:text-amber-400"
            >
              <ArrowLeft className="w-4 h-4" /> Back to home
            </Link>
          </div>
        ) : (
          <form onSubmit={handleSubmit} className="bg-gray-900 border border-gray-800 rounded-2xl p-6 space-y-4">
            <Field label="Work email" required>
              <input
                type="email"
                required
                value={email}
                onChange={e => setEmail(e.target.value)}
                placeholder="you@yourhospital.org"
                className={inputClass}
              />
            </Field>

            <Field label="Your name">
              <input
                type="text"
                value={fullName}
                onChange={e => setFullName(e.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Organization">
              <input
                type="text"
                value={organization}
                onChange={e => setOrganization(e.target.value)}
                placeholder="St Mary's Hospital"
                className={inputClass}
              />
            </Field>

            <Field label="Your role">
              <input
                type="text"
                value={roleRequested}
                onChange={e => setRoleRequested(e.target.value)}
                placeholder="Charge nurse, 3 West"
                className={inputClass}
              />
            </Field>

            <Field label="Anything else">
              <textarea
                rows={3}
                value={note}
                onChange={e => setNote(e.target.value)}
                placeholder="Which schedules or departments you need"
                className={`${inputClass} resize-y`}
              />
            </Field>

            {error && (
              <p className="text-sm text-red-400 bg-red-950/40 border border-red-900/50 rounded-lg px-3 py-2">
                {error}
              </p>
            )}

            <button
              type="submit"
              disabled={state === 'sending' || !email.trim()}
              className="w-full px-6 py-3 bg-amber-600 hover:bg-amber-700 rounded-xl font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
            >
              {state === 'sending' ? 'Sending…' : 'Send request'}
            </button>

            <p className="text-[11px] text-gray-600 text-center leading-relaxed">
              This does not create an account. An administrator has to grant access before
              you can sign in.
            </p>
          </form>
        )}

        <p className="text-center text-sm text-gray-500 mt-6">
          Already have access?{' '}
          <Link to="/login" className="text-amber-500 hover:text-amber-400">Sign in</Link>
        </p>
      </div>
    </div>
  )
}

const inputClass =
  'w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm text-gray-100 ' +
  'placeholder:text-gray-600 focus:outline-none focus:border-amber-600'

function Field({ label, required, children }: {
  label: string
  required?: boolean
  children: React.ReactNode
}) {
  return (
    <div>
      <label className="block text-xs text-gray-400 mb-1.5">
        {label}
        {required && <span className="text-amber-500 ml-1">*</span>}
      </label>
      {children}
    </div>
  )
}
