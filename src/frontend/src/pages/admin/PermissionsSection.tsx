import { useCallback, useEffect, useRef, useState } from 'react'
import { AlertTriangle, CheckCircle, Plus, ShieldCheck, Trash2, UserPlus, Users } from 'lucide-react'
import { identitiesApi, localAccountsApi, permissionsAdminApi, tenantsApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import type { LocalAccount, PermissionGrant, SignInIdentity, Tenant } from '@/types'

const PERMISSION_OPTIONS: { key: string; label: string }[] = [
  { key: 'Schedule.Read', label: 'On-Call Schedule — Read' },
  { key: 'Schedule.Write', label: 'On-Call Schedule — Write' },
  { key: 'Directory.Read', label: 'Directory — Read' },
  { key: 'Directory.Write', label: 'Directory — Write' },
  { key: 'CodeCall.Write', label: 'Code Call — Write' },
]

/**
 * Admin tab for assigning on-call schedule read/write (and directory) permissions to a
 * specific user — including external principals whose Entra tokens carry no roles.
 */
export default function PermissionsSection() {
  const { activeTenantId, tenantIds, canAdminFull } = useAuth()
  const [grants, setGrants] = useState<PermissionGrant[]>([])
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [identities, setIdentities] = useState<SignInIdentity[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const [principal, setPrincipal] = useState('')
  // Only a super admin may grant system-wide; for anyone else the server rejects it, so
  // don't offer it and don't default to it.
  const [tenantId, setTenantId] = useState<number | ''>(
    activeTenantId ?? (canAdminFull ? '' : tenantIds[0] ?? ''),
  )
  const [perms, setPerms] = useState<Set<string>>(new Set(['Schedule.Read', 'Schedule.Write']))
  const tenantTouched = useRef(false)

  const load = useCallback(async () => {
    setLoading(true)
    // Loaded separately. Both used to share one try, so a failure in either was reported
    // as "Failed to load permission grants" -- which named the wrong call, and left the
    // tenant list empty so every subscription on the page read as "Tenant 4".
    try {
      setGrants(await permissionsAdminApi.list(undefined))
    } catch {
      setError('Failed to load permission grants.')
    }

    try {
      setTenants(await tenantsApi.getAll(true))
    } catch {
      // Names only. Every id on this page still renders, just as "Tenant 4" rather than
      // by name, so this is a degraded label and not worth a red banner over the page.
      setTenants([])
    }
    // Separate from the grants load: the identity directory is new, so an older backend
    // (or a mid-deploy slot) returning 404 must not blank out the whole tab.
    try {
      setIdentities(await identitiesApi.list())
    } catch {
      setIdentities([])
    }
    setLoading(false)
  }, [])

  useEffect(() => { load() }, [load])
  // Auto-select the active subscription only until the user picks one themselves.
  useEffect(() => {
    if (activeTenantId != null && !tenantTouched.current) setTenantId(activeTenantId)
  }, [activeTenantId])

  const tenantName = (id?: number) => id == null
    ? 'All tenants (system-wide)'
    : tenants.find(t => t.id === id)?.name ?? `Tenant ${id}`

  const togglePerm = (p: string) =>
    setPerms(prev => { const next = new Set(prev); if (next.has(p)) next.delete(p); else next.add(p); return next })

  async function create() {
    setError(null)
    setMessage(null)
    if (!principal.trim()) { setError('Enter the user email or Entra object id.'); return }
    if (perms.size === 0) { setError('Select at least one permission.'); return }
    try {
      await permissionsAdminApi.create({
        tenantId: tenantId === '' ? undefined : Number(tenantId),
        externalPrincipalId: principal.trim(),
        permissions: [...perms].join(','),
      })
      setPrincipal('')
      await load()
      setMessage('Permission granted.')
      setTimeout(() => setMessage(null), 3000)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to grant permission.')
    }
  }

  async function revoke(g: PermissionGrant, reactivate = false) {
    setError(null)
    try {
      if (reactivate) {
        await permissionsAdminApi.update(g.id, { isActive: true })
        setGrants(prev => prev.map(x => x.id === g.id ? { ...x, isActive: true } : x))
      } else {
        await permissionsAdminApi.remove(g.id)
        setGrants(prev => prev.filter(x => x.id !== g.id))
      }
    } catch {
      setError('Failed to update permission grant.')
    }
  }

  return (
    <div className="space-y-6">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}
      {message && (
        <div className="flex items-center gap-3 bg-green-600/10 border border-green-600/30 rounded-xl px-5 py-3 text-sm text-green-400">
          <CheckCircle className="w-5 h-5 flex-shrink-0" /><span>{message}</span>
        </div>
      )}

      {/* Signed-in users. Entra/Google tokens carry no roles, so people arrive with no
          access and used to be invisible here — the grant field below is free text, with
          no list to pick from. This is that list. */}
      <SignedInUsers
        identities={identities}
        loading={loading}
        tenantName={tenantName}
        onSelect={(id) => {
          // Prefill the existing grant form rather than duplicating it.
          setPrincipal(id.email || id.externalObjectId)
          setMessage(null)
          setError(null)
        }}
      />

      {/* Grant form */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium flex items-center gap-2">
          <ShieldCheck className="w-5 h-5 text-amber-500" /> Grant on-call permission to a user
        </h2>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm text-gray-500 mb-1">User (email or Entra object id)</label>
            <input
              type="text" value={principal} onChange={e => setPrincipal(e.target.value)}
              placeholder="user@hospital.org or object-id"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 font-mono"
            />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Subscription (tenant)</label>
            <select
              value={tenantId} onChange={e => { tenantTouched.current = true; setTenantId(e.target.value ? Number(e.target.value) : '') }}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            >
              {canAdminFull && <option value={''}>All tenants (system-wide)</option>}
              {tenantIds.map(id => <option key={id} value={id}>{tenantName(id)}</option>)}
            </select>
            <p className="text-xs text-gray-600 mt-1">
              {canAdminFull
                ? 'Scope the grant to one subscription, or grant system-wide.'
                : 'Grants are scoped to the subscriptions you administer.'}
            </p>
          </div>
        </div>

        <div>
          <label className="block text-sm text-gray-500 mb-2">Permissions</label>
          <div className="flex flex-wrap gap-2">
            {PERMISSION_OPTIONS.map(opt => {
              const on = perms.has(opt.key)
              return (
                <button
                  key={opt.key}
                  type="button"
                  onClick={() => togglePerm(opt.key)}
                  className={`px-3 py-1.5 rounded-lg text-xs font-medium transition-colors border ${
                    on ? 'bg-amber-600/20 text-amber-400 border-amber-600/40' : 'bg-gray-800 text-gray-400 border-gray-700'
                  }`}
                >
                  {opt.label}
                </button>
              )
            })}
          </div>
        </div>

        <div className="flex justify-end">
          <button
            onClick={create}
            className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
          >
            <Plus className="w-4 h-4" /> Grant Permission
          </button>
        </div>
      </section>

      {/* Grant list */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800">
          <p className="text-sm text-gray-500">{grants.length} permission grant{grants.length !== 1 ? 's' : ''}</p>
        </div>
        {loading ? (
          <div className="flex justify-center py-16"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
        ) : grants.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <ShieldCheck className="w-12 h-12 mb-4 text-gray-700" />
            <p>No permission grants yet</p>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {grants.map(g => (
              <div key={g.id} className="px-5 py-4 flex items-center justify-between">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-sm text-gray-200 truncate">{g.externalPrincipalId}</span>
                    {g.tenantId ? (
                      <span className="text-xs px-2 py-0.5 rounded-full bg-blue-600/20 text-blue-500">{tenantName(g.tenantId)}</span>
                    ) : (
                      <span className="text-xs px-2 py-0.5 rounded-full bg-purple-600/20 text-purple-500">All</span>
                    )}
                  </div>
                  <div className="mt-1 flex flex-wrap gap-1.5">
                    {g.permissions.map(p => (
                      <span key={p} className="text-xs px-2 py-0.5 rounded bg-gray-800 text-gray-300">{p}</span>
                    ))}
                  </div>
                </div>
                <div className="flex items-center gap-2 flex-shrink-0">
                  {!g.isActive && <span className="text-xs px-2 py-0.5 rounded-full bg-red-600/15 text-red-400">Revoked</span>}
                  {g.isActive ? (
                    <button onClick={() => revoke(g)} title="Revoke" className="p-1.5 hover:bg-gray-800 rounded-lg">
                      <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                    </button>
                  ) : (
                    <button onClick={() => revoke(g, true)} className="text-xs text-gray-400 hover:text-gray-200">Restore</button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {/* Local accounts are managed under Admin.Full only, so a sub-admin was shown a
          section that could do nothing but report "Failed to load local accounts". */}
      {canAdminFull && <LocalAccountsSection />}
    </div>
  )
}

function LocalAccountsSection() {
  const [accounts, setAccounts] = useState<LocalAccount[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try { setAccounts(await localAccountsApi.list()) } catch { setError('Failed to load local accounts.') }
    setLoading(false)
  }, [])
  useEffect(() => { load() }, [load])

  async function create() {
    setError(null); setMessage(null)
    if (!email.trim() || password.length < 8) { setError('Email and a password of 8+ characters are required.'); return }
    setSaving(true)
    try {
      await localAccountsApi.create({ email: email.trim(), password, displayName: displayName.trim() || undefined })
      setEmail(''); setPassword(''); setDisplayName('')
      await load()
      setMessage('Local account created.'); setTimeout(() => setMessage(null), 2500)
    } catch (e) { setError(e instanceof Error ? e.message : 'Failed to create local account.') }
    finally { setSaving(false) }
  }

  async function resetPassword(acc: LocalAccount) {
    const next = window.prompt(`New password for ${acc.email} (8+ characters):`)
    if (!next || next.length < 8) { setError('Password must be 8+ characters.'); return }
    setError(null)
    try { await localAccountsApi.resetPassword(acc.id, next); setMessage('Password reset.'); setTimeout(() => setMessage(null), 2500) }
    catch (e) { setError(e instanceof Error ? e.message : 'Failed to reset password.') }
  }

  async function deactivate(acc: LocalAccount) {
    setError(null)
    try { await localAccountsApi.remove(acc.id); setAccounts(prev => prev.filter(a => a.id !== acc.id)) }
    catch { setError('Failed to deactivate the local account.') }
  }

  return (
    <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
      <h2 className="font-medium flex items-center gap-2">
        <ShieldCheck className="w-5 h-5 text-amber-500" /> Local Accounts
      </h2>
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}
      {message && (
        <div className="flex items-center gap-3 bg-green-600/10 border border-green-600/30 rounded-xl px-5 py-3 text-sm text-green-400">
          <CheckCircle className="w-5 h-5 flex-shrink-0" /><span>{message}</span>
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
        <input value={email} onChange={e => setEmail(e.target.value)} placeholder="email@host"
          className="bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600" />
        <input value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="Display name"
          className="bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600" />
        <div className="flex gap-2">
          <input type="password" value={password} onChange={e => setPassword(e.target.value)} placeholder="Password (8+)"
            className="flex-1 bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600" />
          <button onClick={create} disabled={saving}
            className="flex items-center gap-1 px-3 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
            <Plus className="w-4 h-4" />{saving ? '…' : 'Add'}
          </button>
        </div>
      </div>

      <div className="divide-y divide-gray-800">
        {loading ? (
          <div className="flex justify-center py-10"><div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" /></div>
        ) : accounts.length === 0 ? (
          <p className="py-10 text-center text-sm text-gray-500">No local accounts yet.</p>
        ) : accounts.map(a => (
          <div key={a.id} className="py-3 flex items-center justify-between">
            <div className="min-w-0">
              <p className="text-sm font-medium truncate">{a.displayName || a.email}</p>
              <p className="text-xs text-gray-500 truncate">{a.email} · {a.roles.join(', ')}</p>
            </div>
            <div className="flex items-center gap-2 flex-shrink-0">
              <button onClick={() => resetPassword(a)} className="text-xs text-gray-400 hover:text-gray-200">Reset pwd</button>
              <button onClick={() => deactivate(a)} className="text-xs text-red-500 hover:text-red-400">Deactivate</button>
            </div>
          </div>
        ))}
      </div>
    </section>
  )
}
/**
 * People who have signed in, most recent first.
 *
 * Microsoft and Google tokens carry no app roles, so a new user lands with no access and
 * — before this list existed — left no record anywhere, making them impossible to find in
 * the admin UI. Selecting someone fills in the grant form below.
 */
function SignedInUsers({ identities, loading, tenantName, onSelect }: {
  identities: SignInIdentity[]
  loading: boolean
  tenantName: (id?: number) => string
  onSelect: (identity: SignInIdentity) => void
}) {
  const [copied, setCopied] = useState<string | null>(null)

  const relative = (iso: string) => {
    const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000)
    if (mins < 1) return 'just now'
    if (mins < 60) return `${mins}m ago`
    if (mins < 1440) return `${Math.round(mins / 60)}h ago`
    return `${Math.round(mins / 1440)}d ago`
  }

  async function copyObjectId(id: string) {
    try {
      await navigator.clipboard.writeText(id)
      setCopied(id)
      setTimeout(() => setCopied(null), 2000)
    } catch { /* clipboard unavailable */ }
  }

  const waiting = identities.filter(i => i.hasNoAccess).length

  return (
    <section className="bg-gray-900 border border-gray-800 rounded-xl">
      <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between gap-3">
        <h2 className="font-medium flex items-center gap-2">
          <Users className="w-5 h-5 text-amber-500" /> Signed-in users
        </h2>
        {waiting > 0 && (
          <span className="text-xs px-2 py-0.5 rounded-full bg-amber-600/20 text-amber-400">
            {waiting} awaiting access
          </span>
        )}
      </div>

      {loading ? (
        <div className="flex justify-center py-10"><div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" /></div>
      ) : identities.length === 0 ? (
        <div className="flex flex-col items-center py-12 text-gray-500">
          <Users className="w-10 h-10 mb-3 text-gray-700" />
          <p className="text-sm">Nobody has signed in yet</p>
          <p className="text-xs text-gray-600 mt-1">Users appear here the first time they sign in.</p>
        </div>
      ) : (
        <div className="divide-y divide-gray-800">
          {identities.map(i => (
            <div key={i.id} className="px-5 py-4 flex items-center justify-between gap-3">
              <div className="min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-sm font-medium truncate">{i.displayName || i.email || i.externalObjectId}</span>
                  <span className="text-xs px-2 py-0.5 rounded-full bg-gray-800 text-gray-400 capitalize">{i.provider}</span>
                  {i.isSuperAdmin ? (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-purple-600/20 text-purple-400">Super admin</span>
                  ) : i.tenantAdminOf.length > 0 ? (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-blue-600/20 text-blue-400">
                      Sub-admin · {i.tenantAdminOf.map(t => tenantName(t)).join(', ')}
                    </span>
                  ) : i.hasNoAccess ? (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-amber-600/20 text-amber-400">No access</span>
                  ) : (
                    i.permissions.map(p => (
                      <span key={p} className="text-xs px-2 py-0.5 rounded-full bg-green-600/20 text-green-500">{p}</span>
                    ))
                  )}
                </div>
                <p className="text-xs text-gray-500 truncate mt-0.5">
                  {i.email || 'no email on token'} · last seen {relative(i.lastSeenAt)}
                </p>
                {/* The object id is what appointing a sub-admin requires, and it is
                    otherwise impossible to discover. */}
                <button
                  onClick={() => copyObjectId(i.externalObjectId)}
                  title="Copy object id (needed to appoint a sub-admin)"
                  className="text-xs font-mono text-gray-600 hover:text-gray-400 truncate max-w-full mt-0.5"
                >
                  {copied === i.externalObjectId ? 'copied!' : i.externalObjectId}
                </button>
              </div>
              <button
                onClick={() => onSelect(i)}
                className="flex items-center gap-1.5 px-3 py-1.5 bg-gray-800 hover:bg-gray-700 rounded-lg text-xs flex-shrink-0 transition-colors"
              >
                <UserPlus className="w-3.5 h-3.5" />
                {i.hasNoAccess ? 'Grant access' : 'Change access'}
              </button>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
