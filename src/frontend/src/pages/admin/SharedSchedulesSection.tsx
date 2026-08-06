import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle, Link2, Trash2 } from 'lucide-react'
import { sharesApi, tenantsApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import type { PublicShare, Tenant } from '@/types'

/**
 * Admin tab to create, copy, and revoke public permalink shares of the on-call
 * schedule. Each share is coverage-only and revocable; the public viewer sees no
 * names/phones (PHI safe).
 */
export default function SharedSchedulesSection() {
  const { isAdmin, canTenantManage, activeTenantId } = useAuth()
  const [shares, setShares] = useState<PublicShare[]>([])
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const [label, setLabel] = useState('')
  const [tenantId, setTenantId] = useState<number | ''>(activeTenantId ?? '')

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setShares(await sharesApi.list())
    } catch {
      setError('Failed to load public schedule links.')
    }
    setLoading(false)
  }, [])
  useEffect(() => { load() }, [load])
  useEffect(() => { if (activeTenantId != null) setTenantId(activeTenantId) }, [activeTenantId])

  const loadTenants = useCallback(async () => {
    try {
      if (isAdmin || canTenantManage) setTenants(await tenantsApi.getAll(true))
    } catch { /* ignore */ }
  }, [isAdmin, canTenantManage])
  useEffect(() => { loadTenants() }, [loadTenants])

  async function create() {
    setError(null)
    setMessage(null)
    if (tenantId === '') { setError('Choose a subscription (tenant) to share.'); return }
    try {
      const share = await sharesApi.create(Number(tenantId), label.trim() || undefined)
      setShares(prev => [...prev, share])
      setLabel('')
      setMessage('Public schedule link created.')
      setTimeout(() => setMessage(null), 3000)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create the link.')
    }
  }

  function copyLink(share: PublicShare) {
    const url = `${window.location.origin}${share.permalink}`
    navigator.clipboard.writeText(url).then(() => {
      setMessage(`Copied: ${url}`)
      setTimeout(() => setMessage(null), 2500)
    }).catch(() => setError('Could not copy — copy the permalink manually.'))
  }

  async function toggle(share: PublicShare) {
    setError(null)
    try {
      const updated = await sharesApi.setActive(share.id, !share.isActive)
      setShares(prev => prev.map(s => s.id === share.id ? updated : s))
    } catch {
      setError('Failed to update the link.')
    }
  }

  async function remove(share: PublicShare) {
    setError(null)
    try {
      await sharesApi.remove(share.id)
      setShares(prev => prev.filter(s => s.id !== share.id))
    } catch {
      setError('Failed to revoke the link.')
    }
  }

  return (
    <div className="space-y-6 max-w-3xl">
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

      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium flex items-center gap-2">
          <Link2 className="w-5 h-5 text-amber-500" /> Share on-call coverage
        </h2>
        <p className="text-sm text-gray-500">
          Create an unauthenticated permalink showing who is on call by department and tier —
          coverage only, no names or contact details. Revoke anytime.
        </p>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm text-gray-500 mb-1">Subscription (tenant)</label>
            <select
              value={tenantId} onChange={e => setTenantId(e.target.value ? Number(e.target.value) : '')}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            >
              <option value={''}>Select tenant…</option>
              {tenants.filter(t => t.isActive).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Label (optional)</label>
            <input
              type="text" value={label} onChange={e => setLabel(e.target.value)} placeholder="e.g. Residents board"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            />
          </div>
        </div>

        <div className="flex justify-end">
          <button
            onClick={create}
            className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
          >
            <Link2 className="w-4 h-4" /> Create link
          </button>
        </div>
      </section>

      <section className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800">
          <p className="text-sm text-gray-500">{shares.length} public link{shares.length !== 1 ? 's' : ''}</p>
        </div>
        {loading ? (
          <div className="flex justify-center py-16"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
        ) : shares.length === 0 ? (
          <p className="py-12 text-center text-gray-500">No public schedule links yet.</p>
        ) : (
          <div className="divide-y divide-gray-800">
            {shares.map(s => (
              <div key={s.id} className="px-5 py-4">
                <div className="flex items-center justify-between gap-3">
                  <div className="min-w-0">
                    <p className="text-sm font-medium">{s.label || s.tenant || `Share ${s.id}`}</p>
                    <code className="text-xs text-gray-500 font-mono break-all">{s.permalink}</code>
                  </div>
                  <div className="flex items-center gap-2 flex-shrink-0">
                    <button
                      onClick={() => toggle(s)}
                      className={`text-xs px-2 py-1 rounded-lg transition-colors ${
                        s.isActive ? 'bg-green-600/15 text-green-400' : 'bg-gray-800 text-gray-400'
                      }`}
                    >
                      {s.isActive ? 'Active' : 'Disabled'}
                    </button>
                    <button onClick={() => copyLink(s)} className="p-1.5 hover:bg-gray-800 rounded-lg" title="Copy link">
                      <Link2 className="w-4 h-4 text-gray-400 hover:text-amber-400" />
                    </button>
                    <button onClick={() => remove(s)} className="p-1.5 hover:bg-gray-800 rounded-lg" title="Revoke">
                      <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  )
}