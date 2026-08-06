import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle, Plus, ShieldCheck, Trash2 } from 'lucide-react'
import { permissionsAdminApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import type { PermissionGrant } from '@/types'

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
  const { activeTenantId, tenantIds } = useAuth()
  const [grants, setGrants] = useState<PermissionGrant[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const [principal, setPrincipal] = useState('')
  const [tenantId, setTenantId] = useState<number | ''>(activeTenantId ?? '')
  const [perms, setPerms] = useState<Set<string>>(new Set(['Schedule.Read', 'Schedule.Write']))

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setGrants(await permissionsAdminApi.list(undefined))
    } catch {
      setError('Failed to load permission grants.')
    }
    setLoading(false)
  }, [])

  useEffect(() => { load() }, [load])
  useEffect(() => { if (activeTenantId != null) setTenantId(activeTenantId) }, [activeTenantId])

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
              value={tenantId} onChange={e => setTenantId(e.target.value ? Number(e.target.value) : '')}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            >
              <option value={''}>All tenants (system-wide)</option>
              {tenantIds.map(id => <option key={id} value={id}>Tenant {id}</option>)}
            </select>
            <p className="text-xs text-gray-600 mt-1">Existing subscribers shown for context; entering a tenant id assigns scoped.</p>
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
                      <span className="text-xs px-2 py-0.5 rounded-full bg-blue-600/20 text-blue-500">Tenant {g.tenantId}</span>
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
    </div>
  )
}