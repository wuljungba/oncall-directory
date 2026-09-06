import { useMemo, useState } from 'react'
import { AlertTriangle, ShieldCheck, X } from 'lucide-react'
import { permissionsAdminApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import { PERMISSION_OPTIONS, DEFAULT_GRANT_PERMISSIONS } from '@/constants/permissions'
import type { BulkGrantResult, Employee, Tenant } from '@/types'

/**
 * Give the same permissions to everyone selected.
 *
 * Exists because nobody arrives with any. An upload creates directory records and no grants at
 * all, and Entra and Google sign-ins carry no roles either — so without this, provisioning a
 * hundred people means filling in a one-address form a hundred times.
 *
 * Whoever cannot receive a grant is counted before the button is pressed rather than reported
 * afterwards: a grant is keyed to an email address, so a record without one, or a unit line like
 * "3North" that is not a person at all, will be skipped no matter what is chosen here.
 */
export default function BulkPermissionModal({ employees, tenants, onClose, onDone }: {
  employees: Employee[]
  tenants: Tenant[]
  onClose: () => void
  onDone: (result: BulkGrantResult) => void
}) {
  const { canAdminFull, tenantIds, activeTenantId } = useAuth()
  const [tenantId, setTenantId] = useState<number | ''>(
    activeTenantId ?? (canAdminFull ? '' : tenantIds[0] ?? ''),
  )
  const [perms, setPerms] = useState<Set<string>>(new Set(DEFAULT_GRANT_PERMISSIONS))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const togglePerm = (p: string) =>
    setPerms(prev => {
      const next = new Set(prev)
      if (next.has(p)) next.delete(p); else next.add(p)
      return next
    })

  // Computed from rows already loaded, so the skips are visible before the call rather than
  // arriving as a surprise in the result.
  const { grantable, noEmail, notPeople, wrongTenant } = useMemo(() => {
    const notPeople = employees.filter(e => e.contactType === 'Department')
    const rest = employees.filter(e => e.contactType !== 'Department')
    const noEmail = rest.filter(e => !e.email)
    const withEmail = rest.filter(e => e.email)
    const wrongTenant = tenantId === '' ? [] : withEmail.filter(e => e.tenantId !== tenantId)
    return {
      grantable: withEmail.length - wrongTenant.length,
      noEmail: noEmail.length,
      notPeople: notPeople.length,
      wrongTenant: wrongTenant.length,
    }
  }, [employees, tenantId])

  async function submit() {
    if (perms.size === 0) { setError('Select at least one permission.'); return }
    if (grantable === 0) { setError('None of the selected records can hold a permission grant.'); return }

    setBusy(true)
    setError(null)
    try {
      const result = await permissionsAdminApi.bulkGrant({
        tenantId: tenantId === '' ? undefined : Number(tenantId),
        employeeIds: employees.map(e => e.id),
        permissions: [...perms].join(','),
      })
      onDone(result)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not assign permissions.')
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 px-4" onClick={onClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Assign permissions"
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg max-h-[90vh] overflow-y-auto shadow-2xl"
        onClick={e => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-base font-medium flex items-center gap-2">
            <ShieldCheck className="w-4 h-4 text-amber-500" />
            Assign permissions to {employees.length} {employees.length === 1 ? 'person' : 'people'}
          </h2>
          <button onClick={onClose} className="text-gray-500 hover:text-gray-300" aria-label="Close">
            <X className="w-4 h-4" />
          </button>
        </div>

        <div className="px-5 py-4 space-y-4">
          {error && (
            <div className="flex items-center gap-2 bg-red-600/10 border border-red-600/30 rounded-lg px-3 py-2 text-xs text-red-400">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />
              <span>{error}</span>
            </div>
          )}

          <div>
            <label className="block text-xs text-gray-500 mb-1">Subscription</label>
            <select
              value={tenantId}
              onChange={e => setTenantId(e.target.value === '' ? '' : Number(e.target.value))}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600"
            >
              {/* System-wide reaches every subscription there is, so only a full admin is offered it. */}
              {canAdminFull && <option value={''}>All subscriptions (system-wide)</option>}
              {tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
          </div>

          <div>
            <label className="block text-xs text-gray-500 mb-2">Permissions</label>
            <div className="flex flex-wrap gap-2">
              {PERMISSION_OPTIONS.map(p => (
                <button
                  key={p.key}
                  type="button"
                  onClick={() => togglePerm(p.key)}
                  className={`px-3 py-1.5 rounded-lg text-xs border transition-colors ${
                    perms.has(p.key)
                      ? 'bg-amber-600/20 text-amber-400 border-amber-600/40'
                      : 'bg-gray-800 text-gray-400 border-gray-700 hover:border-gray-600'
                  }`}
                >
                  {p.label}
                </button>
              ))}
            </div>
          </div>

          <div className="bg-gray-800/40 rounded-lg px-4 py-3 text-xs text-gray-400 space-y-1">
            <p>
              <span className="text-gray-200 font-medium">{grantable}</span>{' '}
              {grantable === 1 ? 'person gets' : 'people get'} exactly these permissions.
              This <span className="text-gray-200">replaces</span> whatever they already have for
              this subscription.
            </p>
            {noEmail > 0 && <p>{noEmail} skipped — no email address, and a grant is keyed to one.</p>}
            {notPeople > 0 && <p>{notPeople} skipped — unit or service lines, which nobody signs in as.</p>}
            {wrongTenant > 0 && <p>{wrongTenant} skipped — they belong to a different subscription.</p>}
          </div>
        </div>

        <div className="flex justify-end gap-2 px-5 py-4 border-t border-gray-800">
          <button onClick={onClose} className="px-4 py-2 text-sm text-gray-400 hover:text-gray-200">
            Cancel
          </button>
          <button
            onClick={submit}
            disabled={busy || perms.size === 0 || grantable === 0}
            className="px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
          >
            {busy ? 'Assigning…' : `Assign to ${grantable}`}
          </button>
        </div>
      </div>
    </div>
  )
}
