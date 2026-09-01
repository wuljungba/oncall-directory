import { useState, useEffect, useCallback } from 'react'
import { ShieldCheck, ShieldAlert, ShieldX, Clock, AlertTriangle, Check } from 'lucide-react'
import { verificationApi, tenantsApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import type { Tenant, OrganizationVerification } from '@/types'

const ORGANIZATION_TYPES: { value: string; label: string; verifies: boolean }[] = [
  { value: 'Hospital', label: 'Hospital', verifies: true },
  { value: 'Clinic', label: 'Clinic', verifies: true },
  { value: 'PrivatePractice', label: 'Private practice', verifies: true },
  { value: 'SkilledNursing', label: 'Skilled nursing', verifies: true },
  { value: 'EMS', label: 'EMS', verifies: true },
  { value: 'Other', label: 'Something else', verifies: false },
]

/**
 * Where an organization says who it is.
 *
 * A code call reaches real clinicians and a schedule says who is responsible for patients
 * tonight, so a healthcare organization has its NPI checked against the public CMS
 * registry before it can publish either. Declaring a non-healthcare type ends the process
 * rather than starting it — somebody managing a contact list has nothing to verify.
 */
export default function VerificationSection() {
  const { isAdmin, activeTenantId } = useAuth()

  const [tenants, setTenants] = useState<Tenant[]>([])
  const [tenantId, setTenantId] = useState<number | ''>(activeTenantId ?? '')
  const [current, setCurrent] = useState<OrganizationVerification | null>(null)
  const [pending, setPending] = useState<OrganizationVerification[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  const [form, setForm] = useState({
    organizationType: 'Hospital',
    legalName: '',
    doingBusinessAs: '',
    npi: '',
    addressLine1: '',
    city: '',
    state: '',
    postalCode: '',
    stateLicenseNumber: '',
    licenseState: '',
    ein: '',
    representativeName: '',
    representativeTitle: '',
    representativeEmail: '',
  })

  const selectedType = ORGANIZATION_TYPES.find(t => t.value === form.organizationType)
  const needsVerification = selectedType?.verifies ?? true

  const tenant = tenants.find(t => t.id === tenantId) ?? null

  const load = useCallback(async () => {
    try {
      const list = await tenantsApi.getAll()
      setTenants(list)
      if (tenantId === '' && list.length > 0) setTenantId(list[0].id)
    } catch { /* the picker simply stays empty */ }

    if (isAdmin) {
      try {
        setPending(await verificationApi.pending())
      } catch { /* the queue is a convenience, not the point of the page */ }
    }
  }, [isAdmin, tenantId])

  useEffect(() => { load() }, [load])

  useEffect(() => {
    if (tenantId === '') return
    verificationApi.get(Number(tenantId))
      .then(v => {
        setCurrent(v)
        setForm(f => ({
          ...f,
          organizationType: tenants.find(t => t.id === tenantId)?.organizationType ?? f.organizationType,
          legalName: v.legalName ?? '',
          doingBusinessAs: v.doingBusinessAs ?? '',
          npi: v.npi ?? '',
          addressLine1: v.addressLine1 ?? '',
          city: v.city ?? '',
          state: v.state ?? '',
          postalCode: v.postalCode ?? '',
          stateLicenseNumber: v.stateLicenseNumber ?? '',
          licenseState: v.licenseState ?? '',
          ein: v.ein ?? '',
          representativeName: v.representativeName ?? '',
          representativeTitle: v.representativeTitle ?? '',
          representativeEmail: v.representativeEmail ?? '',
        }))
      })
      .catch(() => setCurrent(null))
  }, [tenantId, tenants])

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (tenantId === '') return

    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      await verificationApi.submit(Number(tenantId), form)
      setSaved(true)
      await load()
      const refreshed = await verificationApi.get(Number(tenantId)).catch(() => null)
      setCurrent(refreshed)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not submit.')
    } finally {
      setBusy(false)
    }
  }

  async function decide(id: number, approve: boolean) {
    const reason = approve ? 'Approved by an administrator.' : 'Rejected by an administrator.'
    setBusy(true)
    try {
      if (approve) await verificationApi.approve(id, reason)
      else await verificationApi.reject(id, reason)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not record that decision.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-6">
      <StatusBanner status={tenant?.verificationStatus} />

      {tenants.length > 1 && (
        <div>
          <label htmlFor="verify-tenant" className="block text-xs text-gray-500 mb-1">Subscription</label>
          <select
            id="verify-tenant"
            value={tenantId}
            onChange={e => setTenantId(e.target.value ? Number(e.target.value) : '')}
            className="w-full max-w-md bg-gray-900 border border-gray-800 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600"
          >
            {tenants.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </div>
      )}

      {current?.registryFindings && (
        <div className="text-sm text-gray-400 bg-gray-800/40 rounded-lg px-4 py-3">
          <p className="text-xs uppercase tracking-wide text-gray-600 mb-1">What the checks found</p>
          {current.registryFindings}
        </div>
      )}

      <form onSubmit={submit} className="space-y-4 max-w-2xl">
        <div>
          <label htmlFor="org-type" className="block text-xs text-gray-500 mb-1">Kind of organization</label>
          <select
            id="org-type"
            value={form.organizationType}
            onChange={e => setForm({ ...form, organizationType: e.target.value })}
            className="w-full bg-gray-900 border border-gray-800 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600"
          >
            {ORGANIZATION_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
          </select>
          {!needsVerification && (
            <p className="text-xs text-gray-600 mt-1">
              Nothing to verify for this kind of organization — saving this is all that is needed.
            </p>
          )}
        </div>

        <Field label="Legal name" required value={form.legalName}
          onChange={v => setForm({ ...form, legalName: v })} />

        {needsVerification && (
          <>
            <Field label="Doing business as (optional)" value={form.doingBusinessAs}
              onChange={v => setForm({ ...form, doingBusinessAs: v })} />

            <Field label="Organizational (Type 2) NPI" value={form.npi}
              onChange={v => setForm({ ...form, npi: v })}
              hint="Ten digits. Checked against the public CMS registry, which is what makes this more than a form." />

            <Field label="Street address" value={form.addressLine1}
              onChange={v => setForm({ ...form, addressLine1: v })} />

            <div className="grid grid-cols-3 gap-3">
              <Field label="City" value={form.city} onChange={v => setForm({ ...form, city: v })} />
              <Field label="State" value={form.state} onChange={v => setForm({ ...form, state: v })} />
              <Field label="ZIP" value={form.postalCode} onChange={v => setForm({ ...form, postalCode: v })} />
            </div>

            <div className="grid grid-cols-2 gap-3">
              <Field label="State licence number" value={form.stateLicenseNumber}
                onChange={v => setForm({ ...form, stateLicenseNumber: v })} />
              <Field label="Issuing state" value={form.licenseState}
                onChange={v => setForm({ ...form, licenseState: v })} />
            </div>

            <Field label="EIN (optional)" value={form.ein} onChange={v => setForm({ ...form, ein: v })} />

            <div className="grid grid-cols-2 gap-3">
              <Field label="Authorized representative" value={form.representativeName}
                onChange={v => setForm({ ...form, representativeName: v })} />
              <Field label="Their title" value={form.representativeTitle}
                onChange={v => setForm({ ...form, representativeTitle: v })} />
            </div>

            <Field label="Their work email" value={form.representativeEmail}
              onChange={v => setForm({ ...form, representativeEmail: v })}
              hint="Must be on your organization's own domain — a personal address cannot be checked against anything." />
          </>
        )}

        {error && (
          <p className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-2.5">
            <AlertTriangle className="w-4 h-4 flex-shrink-0" /> {error}
          </p>
        )}

        {saved && !error && (
          <p className="flex items-center gap-2 text-sm text-green-400 bg-green-600/10 rounded-lg px-4 py-2.5">
            <Check className="w-4 h-4 flex-shrink-0" /> Submitted.
          </p>
        )}

        <button
          type="submit"
          disabled={busy || tenantId === ''}
          className="px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
        >
          {busy ? 'Checking…' : 'Submit for verification'}
        </button>
      </form>

      {isAdmin && pending.length > 0 && (
        <div className="pt-6 border-t border-gray-800 space-y-3">
          <h3 className="text-sm font-medium">Waiting on a decision</h3>
          {pending.map(v => (
            <div key={v.id} className="bg-gray-800/40 rounded-lg px-4 py-3 space-y-2">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <p className="text-sm font-medium truncate">{v.legalName}</p>
                  <p className="text-xs text-gray-500">
                    NPI {v.npi || '—'} · {v.representativeEmail || 'no representative address'}
                  </p>
                </div>
                <div className="flex gap-2 shrink-0">
                  <button
                    onClick={() => decide(v.tenantId, true)}
                    disabled={busy}
                    className="px-3 py-1.5 text-xs bg-green-600/20 text-green-400 hover:bg-green-600/30 rounded-lg transition-colors disabled:opacity-50"
                  >
                    Approve
                  </button>
                  <button
                    onClick={() => decide(v.tenantId, false)}
                    disabled={busy}
                    className="px-3 py-1.5 text-xs bg-red-600/20 text-red-400 hover:bg-red-600/30 rounded-lg transition-colors disabled:opacity-50"
                  >
                    Reject
                  </button>
                </div>
              </div>
              {v.registryFindings && <p className="text-xs text-gray-500">{v.registryFindings}</p>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function StatusBanner({ status }: { status?: string }) {
  if (!status || status === 'Verified') {
    return (
      <div className="flex items-center gap-2 text-sm text-green-400 bg-green-600/10 rounded-lg px-4 py-3">
        <ShieldCheck className="w-4 h-4 flex-shrink-0" />
        Verified. Schedules, the directory and code calls all work normally.
      </div>
    )
  }

  if (status === 'Pending') {
    return (
      <div className="flex items-start gap-2 text-sm text-amber-400 bg-amber-600/10 rounded-lg px-4 py-3">
        <Clock className="w-4 h-4 flex-shrink-0 mt-0.5" />
        <span>
          Waiting on a decision. You can read everything; publishing schedules, editing the
          directory and firing code calls stay locked until this is settled.
        </span>
      </div>
    )
  }

  if (status === 'Rejected') {
    return (
      <div className="flex items-start gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
        <ShieldX className="w-4 h-4 flex-shrink-0 mt-0.5" />
        <span>Not verified. Correct the details below and submit again.</span>
      </div>
    )
  }

  return (
    <div className="flex items-start gap-2 text-sm text-gray-400 bg-gray-800/40 rounded-lg px-4 py-3">
      <ShieldAlert className="w-4 h-4 flex-shrink-0 mt-0.5" />
      <span>
        Not yet verified. Reading works; publishing schedules, editing the directory and
        firing code calls are locked until it is.
      </span>
    </div>
  )
}

function Field({
  label, value, onChange, required, hint,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  required?: boolean
  hint?: string
}) {
  const id = `verify-${label.toLowerCase().replace(/[^a-z]+/g, '-')}`
  return (
    <div>
      <label htmlFor={id} className="block text-xs text-gray-500 mb-1">{label}</label>
      <input
        id={id}
        type="text"
        required={required}
        value={value}
        onChange={e => onChange(e.target.value)}
        className="w-full bg-gray-900 border border-gray-800 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600"
      />
      {hint && <p className="text-xs text-gray-600 mt-1">{hint}</p>}
    </div>
  )
}
