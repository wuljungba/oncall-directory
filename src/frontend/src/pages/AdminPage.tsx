import { useState, useEffect } from 'react'
import {
  Users, Building2, RefreshCw, Shield, Plus, Search, X, Save, Trash2,
  CheckCircle, AlertTriangle, Calendar, Phone, Building,
} from 'lucide-react'
import { adminApi, integrationsApi, settingsApi, scheduleApi, tenantsApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import { formatDateOnly } from '@/utils/date'
import type { Employee, Department, TimeOff, Tenant, TenantAdmin, ConnectionStatus } from '@/types'
import CodeCallLocationsSection from './CodeCallLocationsSection'
import PermissionsSection from './admin/PermissionsSection'
import SharedSchedulesSection from './admin/SharedSchedulesSection'
import OnboardingHealthSection from './admin/OnboardingHealthSection'
import OnCallAuditSection from './admin/OnCallAuditSection'

type Tab = 'overview' | 'accounts' | 'departments' | 'integrations' | 'timeoff' | 'locations' | 'tenants' | 'permissions' | 'shares' | 'onboarding' | 'audit'

export default function AdminPage() {
  const [tab, setTab] = useState<Tab>('overview')
  const { activeTenantId, setActiveTenantId, tenantIds, isAdmin, canTenantManage, canAdminScoped } = useAuth()
  const [tenants, setTenants] = useState<Tenant[]>([])

  // Load tenant list for the tenant selector
  useEffect(() => {
    if (canTenantManage || (canAdminScoped && tenantIds.length > 1)) {
      tenantsApi.getAll().then(setTenants).catch(() => {})
    } else {
      // Build tenant list from tenantIds/tenantRoles for scoped admins
      const list = tenantIds.map(id => ({ id, name: `Tenant ${id}`, isActive: true } as Tenant))
      setTenants(list)
    }
  }, [canTenantManage, canAdminScoped, tenantIds])

  const tabs: { key: Tab; label: string }[] = [
    { key: 'overview', label: 'Overview' },
    { key: 'accounts', label: 'Accounts' },
    { key: 'departments', label: 'Departments' },
    { key: 'integrations', label: 'Integrations' },
    { key: 'timeoff', label: 'Time Off' },
    { key: 'locations', label: 'Code Call Locations' },
  ]

  // Add user/permission + subscriptions tabs for admins (full or tenant-manage)
  if (isAdmin || canTenantManage) {
    tabs.push({ key: 'permissions', label: 'Users & Permissions' })
    tabs.push({ key: 'shares', label: 'Public Schedule' })
    tabs.push({ key: 'tenants', label: 'Subscriptions' })
    tabs.push({ key: 'onboarding', label: 'Onboarding' })
    tabs.push({ key: 'audit', label: 'On-Call Audit' })
  }

  // Get the current tenant name for display
  const currentTenantName = activeTenantId
    ? tenants.find(t => t.id === activeTenantId)?.name ?? `Tenant ${activeTenantId}`
    : null

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Admin</h1>
        {currentTenantName && (
          <span className="text-xs px-3 py-1.5 rounded-full bg-amber-600/20 text-amber-500 flex items-center gap-1.5">
            <Building className="w-3.5 h-3.5" />
            Scoped to: {currentTenantName}
          </span>
        )}
      </div>

      {/* Tenant selector — visible when user has multiple tenants or is scoped admin */}
      {(canTenantManage || (canAdminScoped && tenantIds.length > 1)) && (
        <div className="flex items-center gap-2">
          <span className="text-xs text-gray-500">Tenant:</span>
          <select
            value={activeTenantId ?? ''}
            onChange={e => setActiveTenantId(e.target.value ? Number(e.target.value) : null)}
            className="bg-gray-900 border border-gray-700 rounded-lg px-3 py-1.5 text-sm focus:outline-none focus:border-amber-600"
          >
            {canTenantManage && <option value="">All Tenants</option>}
            {tenants.filter(t => t.isActive).map(t => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </div>
      )}

      {/* Tab bar */}
      <div className="flex gap-1 bg-gray-900 border border-gray-800 rounded-xl p-1 w-fit flex-wrap">
        {tabs.map(t => (
          <button
            key={t.key}
            onClick={() => setTab(t.key)}
            className={`px-4 py-2 rounded-lg text-sm font-medium transition-colors ${
              tab === t.key ? 'bg-amber-600/20 text-amber-500' : 'text-gray-400 hover:text-gray-200'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {/* Tab content */}
      {tab === 'overview' && <AdminOverview onSelectTab={setTab} />}
      {tab === 'accounts' && <AccountsSection tenants={tenants} canPickTenant={isAdmin || canTenantManage} activeTenantId={activeTenantId} />}
      {tab === 'departments' && <DepartmentsSection tenants={tenants} canPickTenant={isAdmin || canTenantManage} activeTenantId={activeTenantId} />}
      {tab === 'integrations' && <IntegrationsSection />}
      {tab === 'timeoff' && <TimeOffApprovalSection />}
      {tab === 'locations' && <CodeCallLocationsSection />}
      {tab === 'tenants' && <TenantsSection setActiveTenantId={setActiveTenantId} />}
      {tab === 'permissions' && <PermissionsSection />}
      {tab === 'shares' && <SharedSchedulesSection />}
      {tab === 'onboarding' && <OnboardingHealthSection />}
      {tab === 'audit' && <OnCallAuditSection />}
    </div>
  )
}

// ─── OVERVIEW ────────────────────────────────────────────────────────────

function AdminOverview({ onSelectTab }: { onSelectTab: (tab: Tab) => void }) {
  const [stats, setStats] = useState({ total: 0, active: 0, departments: 0 })
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    async function load() {
      try {
        const [allEmps, allDepts] = await Promise.all([
          adminApi.getAllEmployees(true),
          adminApi.getAllDepartments(true),
        ])
        setStats({
          total: allEmps.length,
          active: allEmps.filter(e => e.isActive).length,
          departments: allDepts.length,
        })
      } catch { /* ignore */ }
      setLoading(false)
    }
    load()
  }, [])

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Stat cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Total Employees</p>
              <p className="text-3xl font-bold text-amber-500 mt-1">{stats.total}</p>
            </div>
            <Users className="w-10 h-10 text-amber-600/30" />
          </div>
        </div>
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Active Employees</p>
              <p className="text-3xl font-bold text-green-500 mt-1">{stats.active}</p>
            </div>
            <CheckCircle className="w-10 h-10 text-green-600/30" />
          </div>
        </div>
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Departments</p>
              <p className="text-3xl font-bold text-blue-500 mt-1">{stats.departments}</p>
            </div>
            <Building2 className="w-10 h-10 text-blue-600/30" />
          </div>
        </div>
      </div>

      {/* Feature cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <AdminFeatureCard
          icon={Users}
          iconColor="text-amber-500"
          title="Accounts"
          description="Manage all employee accounts. Create, edit, or deactivate user profiles."
          onClick={() => onSelectTab('accounts')}
        />
        <AdminFeatureCard
          icon={Building2}
          iconColor="text-blue-500"
          title="Departments"
          description="Organize employees into departments and manage sub-account structure."
          onClick={() => onSelectTab('departments')}
        />
        <AdminFeatureCard
          icon={RefreshCw}
          iconColor="text-green-500"
          title="Integrations"
          description="Configure Microsoft 365 connections, sync Active Directory, and manage notification channels."
          onClick={() => onSelectTab('integrations')}
        />
      </div>

      {/* Policies section */}
      <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
        <h2 className="font-medium mb-4 flex items-center gap-2">
          <Shield className="w-5 h-5 text-amber-500" />
          Security & Compliance
        </h2>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-sm">
          <div className="p-4 bg-gray-800/50 rounded-lg">
            <h3 className="font-medium text-amber-400 mb-1">HIPAA Compliance</h3>
            <p className="text-gray-400">All PHI fields column-encrypted. Access audited via immutable logs. Sessions auto-expire.</p>
          </div>
          <div className="p-4 bg-gray-800/50 rounded-lg">
            <h3 className="font-medium text-blue-400 mb-1">Role-Based Access</h3>
            <p className="text-gray-400">Three tiers: Viewer, Scheduler, Admin. Roles assigned via Azure AD app roles.</p>
          </div>
          <div className="p-4 bg-gray-800/50 rounded-lg">
            <h3 className="font-medium text-green-400 mb-1">Data Encryption</h3>
            <p className="text-gray-400">All traffic TLS-encrypted. Authentication via Microsoft Entra ID JWT bearer tokens.</p>
          </div>
        </div>
      </div>
    </div>
  )
}

function AdminFeatureCard({ icon: Icon, iconColor, title, description, onClick }: {
  icon: React.ElementType
  iconColor: string
  title: string
  description: string
  onClick: () => void
}) {
  return (
    <button
      onClick={onClick}
      className="text-left bg-gray-900 border border-gray-800 rounded-xl p-5 hover:border-amber-600/50 transition-colors group"
    >
      <Icon className={`w-8 h-8 ${iconColor}`} />
      <h3 className="font-medium mt-2 group-hover:text-amber-400 transition-colors">{title}</h3>
      <p className="text-sm text-gray-500 mt-1">{description}</p>
    </button>
  )
}

// ─── ACCOUNTS ────────────────────────────────────────────────────────────

function AccountsSection({ tenants, canPickTenant, activeTenantId }: {
  tenants: Tenant[]
  canPickTenant: boolean
  activeTenantId: number | null
}) {
  const [employees, setEmployees] = useState<Employee[]>([])
  const [departments, setDepartments] = useState<Department[]>([])
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [showModal, setShowModal] = useState(false)
  const [editingEmployee, setEditingEmployee] = useState<Employee | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { loadData() }, [])

  async function loadData() {
    setLoading(true)
    try {
      const [emps, depts] = await Promise.all([
        adminApi.getAllEmployees(true),
        adminApi.getAllDepartments(),
      ])
      setEmployees(emps)
      setDepartments(depts)
    } catch { setError('Failed to load employees.') }
    setLoading(false)
  }

  const filtered = employees.filter(e => {
    if (!search) return true
    const q = search.toLowerCase()
    return e.firstName.toLowerCase().includes(q)
      || e.lastName.toLowerCase().includes(q)
      || e.email.toLowerCase().includes(q)
      || (e.title && e.title.toLowerCase().includes(q))
  })

  async function handleSave(data: Partial<Employee>) {
    try {
      setError(null)
      if (editingEmployee) {
        const updated = await adminApi.updateEmployee(editingEmployee.id, data as Record<string, unknown>)
        setEmployees(prev => prev.map(e => e.id === updated.id ? updated : e))
      } else {
        const created = await adminApi.createEmployee(data as Record<string, unknown>)
        setEmployees(prev => [...prev, created])
      }
      setShowModal(false)
      setEditingEmployee(null)
    } catch { setError('Failed to save employee.') }
  }

  async function handleToggleActive(emp: Employee) {
    try {
      setError(null)
      if (emp.isActive) {
        await adminApi.deactivateEmployee(emp.id)
        setEmployees(prev => prev.map(e => e.id === emp.id ? { ...e, isActive: false } : e))
      } else {
        await adminApi.reactivateEmployee(emp.id)
        setEmployees(prev => prev.map(e => e.id === emp.id ? { ...e, isActive: true } : e))
      }
    } catch { setError('Failed to update employee status.') }
  }

  async function handleDeleteEmployee(emp: Employee) {
    const ok = window.confirm(
      `Permanently delete ${emp.firstName} ${emp.lastName} (${emp.email})?\n\nThis cannot be undone. If they're referenced by schedules, time-off, or phone trees, deletion will be blocked.`
    )
    if (!ok) return
    try {
      setError(null)
      await adminApi.hardDeleteEmployee(emp.id)
      setEmployees(prev => prev.filter(e => e.id !== emp.id))
    } catch {
      setError('Delete blocked — this employee is referenced by schedule, time-off, or phone-tree history. Deactivate instead.')
    }
  }

  if (loading) return <div className="flex items-center justify-center py-20"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>

  return (
    <div className="space-y-4">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      <div className="flex items-center justify-between gap-4">
        <div className="relative flex-1 max-w-md">
          <Search className="absolute left-4 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-500" />
          <input
            type="text" placeholder="Search by name, email, or title..."
            value={search} onChange={e => setSearch(e.target.value)}
            className="w-full bg-gray-900 border border-gray-700 rounded-xl pl-11 pr-4 py-2.5 text-sm focus:outline-none focus:border-amber-600"
          />
        </div>
        <button
          onClick={() => { setEditingEmployee(null); setShowModal(true) }}
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
        >
          <Plus className="w-4 h-4" /> Add Employee
        </button>
      </div>

      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800">
          <p className="text-sm text-gray-500">{filtered.length} employee{filtered.length !== 1 ? 's' : ''}</p>
        </div>
        {filtered.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <Users className="w-12 h-12 mb-4 text-gray-700" />
            <p>No employees found</p>
            <button
              onClick={() => { setEditingEmployee(null); setShowModal(true) }}
              className="mt-4 flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Plus className="w-4 h-4" /> Add Employee
            </button>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {filtered.map(emp => (
              <div key={emp.id} className="px-5 py-4 flex items-center justify-between group">
                <div className="flex items-center gap-3 min-w-0">
                  <div className="w-10 h-10 rounded-full bg-gray-700 flex items-center justify-center text-sm font-medium flex-shrink-0">
                    {emp.firstName?.charAt(0)}{emp.lastName?.charAt(0)}
                  </div>
                  <div className="min-w-0">
                    <p className="text-sm font-medium truncate">{emp.firstName} {emp.lastName}</p>
                    <p className="text-xs text-gray-500 truncate">
                      {emp.email}
                      {emp.department ? ` · ${emp.department.name}` : ''}
                    </p>
                  </div>
                </div>
                <div className="flex items-center gap-3 flex-shrink-0">
                  {!emp.isActive && (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-red-600/20 text-red-500">Inactive</span>
                  )}
                  {emp.onCallStatus && (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-amber-600/20 text-amber-500">On Call</span>
                  )}
                  <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                    <button
                      onClick={() => { setEditingEmployee(emp); setShowModal(true) }}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors" title="Edit"
                    >
                      <svg className="w-4 h-4 text-gray-400 hover:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                      </svg>
                    </button>
                    <button
                      onClick={() => handleToggleActive(emp)}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors"
                      title={emp.isActive ? 'Deactivate' : 'Reactivate'}
                    >
                      {emp.isActive
                        ? <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                        : <CheckCircle className="w-4 h-4 text-gray-400 hover:text-green-400" />
                      }
                    </button>
                    <button
                      onClick={() => handleDeleteEmployee(emp)}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors"
                      title="Delete permanently"
                    >
                      <X className="w-4 h-4 text-gray-400 hover:text-red-400" />
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {showModal && (
        <EmployeeFormModal
          employee={editingEmployee}
          departments={departments}
          employees={employees}
          tenants={tenants}
          canPickTenant={canPickTenant}
          activeTenantId={activeTenantId}
          onSave={handleSave}
          onClose={() => { setShowModal(false); setEditingEmployee(null) }}
        />
      )}
    </div>
  )
}

function EmployeeFormModal({ employee, departments, employees, tenants, canPickTenant, activeTenantId, onSave, onClose }: {
  employee: Employee | null
  departments: Department[]
  employees: Employee[]
  tenants: Tenant[]
  canPickTenant: boolean
  activeTenantId: number | null
  onSave: (data: Partial<Employee>) => Promise<void>
  onClose: () => void
}) {
  const [firstName, setFirstName] = useState(employee?.firstName || '')
  const [lastName, setLastName] = useState(employee?.lastName || '')
  const [email, setEmail] = useState(employee?.email || '')
  const [title, setTitle] = useState(employee?.title || '')
  const [specialty, setSpecialty] = useState(employee?.specialty || '')
  const [clinicalRole, setClinicalRole] = useState(employee?.clinicalRole || '')
  const [officePhone, setOfficePhone] = useState(employee?.officePhone || '')
  const [mobilePhone, setMobilePhone] = useState(employee?.mobilePhone || '')
  const [officeLocation, setOfficeLocation] = useState(employee?.officeLocation || '')
  const [departmentId, setDepartmentId] = useState<number | ''>(employee?.departmentId ?? '')
  const [tenantId, setTenantId] = useState<number | ''>(employee?.tenantId ?? activeTenantId ?? '')
  const [managerId, setManagerId] = useState<string | ''>(employee?.managerId ?? '')
  const [isActive, setIsActive] = useState(employee?.isActive ?? true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!firstName.trim() || !lastName.trim() || !email.trim()) {
      setError('First name, last name, and email are required.')
      return
    }
    setSaving(true)
    setError(null)
    try {
      await onSave({
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        email: email.trim(),
        title: title || undefined,
        specialty: specialty || undefined,
        clinicalRole: clinicalRole || undefined,
        officePhone: officePhone || undefined,
        mobilePhone: mobilePhone || undefined,
        officeLocation: officeLocation || undefined,
        departmentId: departmentId || undefined,
        managerId: managerId ? managerId as unknown as number : undefined,
        tenantId: canPickTenant ? tenantId || undefined : undefined,
        isActive,
      } as Partial<Employee>)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-2xl mx-4 max-h-[90vh] overflow-y-auto" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-800 sticky top-0 bg-gray-900 z-10">
          <h2 className="text-lg font-medium">{employee ? 'Edit Employee' : 'Add Employee'}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg transition-colors"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-6 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />{error}
            </div>
          )}

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">First Name *</label>
              <input type="text" required value={firstName} onChange={e => setFirstName(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Last Name *</label>
              <input type="text" required value={lastName} onChange={e => setLastName(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Email *</label>
            <input type="email" required value={email} onChange={e => setEmail(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Title</label>
              <input type="text" value={title} onChange={e => setTitle(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Specialty</label>
              <input type="text" value={specialty} onChange={e => setSpecialty(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Clinical Role</label>
              <input type="text" value={clinicalRole} onChange={e => setClinicalRole(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Department</label>
              <select value={departmentId} onChange={e => setDepartmentId(e.target.value ? Number(e.target.value) : '')}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
                <option value="">None</option>
                {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
              </select>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Office Phone</label>
              <input type="text" value={officePhone} onChange={e => setOfficePhone(e.target.value)} placeholder="+12025551234"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Mobile Phone</label>
              <input type="text" value={mobilePhone} onChange={e => setMobilePhone(e.target.value)} placeholder="+12025551234"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Office Location</label>
            <input type="text" value={officeLocation} onChange={e => setOfficeLocation(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Manager</label>
              <select value={managerId} onChange={e => setManagerId(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
                <option value="">None</option>
                {employees.filter(e => e.id !== employee?.id).map(e =>
                  <option key={e.id} value={e.id}>{e.firstName} {e.lastName}</option>
                )}
              </select>
            </div>
            {employee && (
              <div>
                <label className="block text-sm text-gray-500 mb-1">Status</label>
                <div className="flex items-center gap-3 h-full pt-1">
                  <button
                    type="button"
                    onClick={() => setIsActive(!isActive)}
                    className={`relative w-10 h-6 rounded-full transition-colors ${isActive ? 'bg-amber-600' : 'bg-gray-700'}`}
                  >
                    <div className={`absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white transition-transform ${isActive ? 'translate-x-4' : 'translate-x-0'}`} />
                  </button>
                  <span className="text-sm text-gray-400">{isActive ? 'Active' : 'Inactive'}</span>
                </div>
              </div>
            )}
          </div>

          {canPickTenant && (
            <div>
              <label className="block text-sm text-gray-500 mb-1">Subscription (tenant)</label>
              <select value={tenantId} onChange={e => setTenantId(e.target.value ? Number(e.target.value) : '')}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
                <option value="">Unassigned</option>
                {tenants.filter(t => t.isActive).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select>
              <p className="text-xs text-gray-600 mt-1">Choose the subscription this user belongs to (super admin only).</p>
            </div>
          )}

          <div className="flex justify-end gap-2 pt-4 border-t border-gray-800">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : employee ? 'Save Changes' : 'Create Employee'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

// ─── DEPARTMENTS ─────────────────────────────────────────────────────────

function DepartmentsSection({ tenants, canPickTenant, activeTenantId }: {
  tenants: Tenant[]
  canPickTenant: boolean
  activeTenantId: number | null
}) {
  const [departments, setDepartments] = useState<Department[]>([])
  const [loading, setLoading] = useState(true)
  const [showModal, setShowModal] = useState(false)
  const [editingDepartment, setEditingDepartment] = useState<Department | null>(null)
  const [showMembers, setShowMembers] = useState<Department | null>(null)
  const [members, setMembers] = useState<Employee[]>([])
  const [membersLoading, setMembersLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { loadData() }, [])

  async function loadData() {
    try {
      setDepartments(await adminApi.getAllDepartments(true))
    } catch { setError('Failed to load departments.') }
    setLoading(false)
  }

  async function handleSave(data: Partial<Department>) {
    try {
      setError(null)
      if (editingDepartment) {
        const updated = await adminApi.updateDepartment(editingDepartment.id, data as Record<string, unknown>)
        setDepartments(prev => prev.map(d => d.id === updated.id ? updated : d))
      } else {
        const created = await adminApi.createDepartment(data as Record<string, unknown>)
        setDepartments(prev => [...prev, created])
      }
      setShowModal(false)
      setEditingDepartment(null)
    } catch { setError('Failed to save department.') }
  }

  async function handleToggleActive(dept: Department) {
    try {
      setError(null)
      if (dept.isActive) {
        await adminApi.deactivateDepartment(dept.id)
        setDepartments(prev => prev.map(d => d.id === dept.id ? { ...d, isActive: false } : d))
      } else {
        // Reactivate via update
        await adminApi.updateDepartment(dept.id, { isActive: true } as Record<string, unknown>)
        setDepartments(prev => prev.map(d => d.id === dept.id ? { ...d, isActive: true } : d))
      }
    } catch { setError('Failed to update department.') }
  }

  async function handleViewMembers(dept: Department) {
    setShowMembers(dept)
    setMembersLoading(true)
    try {
      setMembers(await adminApi.getDepartmentMembers(dept.id))
    } catch { setMembers([]) }
    setMembersLoading(false)
  }

  if (loading) return <div className="flex items-center justify-center py-20"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>

  return (
    <div className="space-y-4">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-500">{departments.length} department{departments.length !== 1 ? 's' : ''}</p>
        <button
          onClick={() => { setEditingDepartment(null); setShowModal(true) }}
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
        >
          <Plus className="w-4 h-4" /> Add Department
        </button>
      </div>

      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        {departments.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <Building2 className="w-12 h-12 mb-4 text-gray-700" />
            <p>No departments configured</p>
            <button
              onClick={() => { setEditingDepartment(null); setShowModal(true) }}
              className="mt-4 flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Plus className="w-4 h-4" /> Add Department
            </button>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {departments.map(dept => (
              <div key={dept.id} className="px-5 py-4 flex items-center justify-between group">
                <div>
                  <p className="text-sm font-medium">{dept.name}{dept.category && (<span className="text-xs px-2 py-0.5 rounded-full bg-amber-600/20 text-amber-500 ml-2 font-normal">{dept.category}</span>)}</p>
                  <p className="text-xs text-gray-500 mt-0.5">{dept.description || 'No description'}</p>
                </div>
                <div className="flex items-center gap-3">
                  {!dept.isActive && (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-red-600/20 text-red-500">Inactive</span>
                  )}
                  <button
                    onClick={() => handleViewMembers(dept)}
                    className="text-xs text-gray-500 hover:text-gray-300 transition-colors flex items-center gap-1"
                  >
                    <Users className="w-3 h-3" /> View Members
                  </button>
                  <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                    <button
                      onClick={() => { setEditingDepartment(dept); setShowModal(true) }}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors" title="Edit"
                    >
                      <svg className="w-4 h-4 text-gray-400 hover:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                      </svg>
                    </button>
                    <button
                      onClick={() => handleToggleActive(dept)}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors"
                      title={dept.isActive ? 'Deactivate' : 'Reactivate'}
                    >
                      {dept.isActive
                        ? <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                        : <CheckCircle className="w-4 h-4 text-gray-400 hover:text-green-400" />
                      }
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Department Form Modal */}
      {showModal && (
        <DepartmentFormModal
          department={editingDepartment}
          tenants={tenants}
          canPickTenant={canPickTenant}
          activeTenantId={activeTenantId}
          onSave={handleSave}
          onClose={() => { setShowModal(false); setEditingDepartment(null) }}
        />
      )}

      {/* View Members Modal */}
      {showMembers && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={() => setShowMembers(null)}>
          <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
            <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
              <h2 className="text-lg font-medium">{showMembers.name} — Members</h2>
              <button onClick={() => setShowMembers(null)} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
            </div>
            <div className="p-5 max-h-[400px] overflow-y-auto">
              {membersLoading ? (
                <div className="flex justify-center py-8"><div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" /></div>
              ) : members.length === 0 ? (
                <p className="text-center text-gray-500 py-8">No members in this department.</p>
              ) : (
                <div className="space-y-3">
                  {members.map(m => (
                    <div key={m.id} className="flex items-center gap-3">
                      <div className="w-8 h-8 rounded-full bg-gray-700 flex items-center justify-center text-xs font-medium">
                        {m.firstName?.charAt(0)}{m.lastName?.charAt(0)}
                      </div>
                      <div>
                        <p className="text-sm font-medium">{m.firstName} {m.lastName}</p>
                        <p className="text-xs text-gray-500">{m.title || m.email}</p>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

const CATEGORIES = ['Healthcare', 'Technology', 'Corporate', 'Business', 'Operations', 'Education', 'Manufacturing', 'Retail', 'Other']

function DepartmentFormModal({ department, tenants, canPickTenant, activeTenantId, onSave, onClose }: {
  department: Department | null
  tenants: Tenant[]
  canPickTenant: boolean
  activeTenantId: number | null
  onSave: (data: Partial<Department>) => Promise<void>
  onClose: () => void
}) {
  const [name, setName] = useState(department?.name || '')
  const [description, setDescription] = useState(department?.description || '')
  const [category, setCategory] = useState(department?.category || '')
  const [tenantId, setTenantId] = useState<number | ''>(department?.tenantId ?? activeTenantId ?? '')
  const [isActive, setIsActive] = useState(department?.isActive ?? true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Department name is required.'); return }
    setSaving(true)
    setError(null)
    try {
      await onSave({ name: name.trim(), description: description.trim() || undefined, category: category || undefined, tenantId: canPickTenant ? tenantId || undefined : undefined, isActive })
    } catch { setError('Failed to save department.') }
    finally { setSaving(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{department ? 'Edit Department' : 'Add Department'}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Name *</label>
            <input type="text" required value={name} onChange={e => setName(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Description</label>
            <textarea value={description} onChange={e => setDescription(e.target.value)} rows={3}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Industry Category</label>
            <select value={category} onChange={e => setCategory(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
              <option value="">None</option>
              {CATEGORIES.map(c => <option key={c} value={c}>{c}</option>)}
            </select>
          </div>
          {canPickTenant && (
            <div>
              <label className="block text-sm text-gray-500 mb-1">Subscription (tenant)</label>
              <select value={tenantId} onChange={e => setTenantId(e.target.value ? Number(e.target.value) : '')}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
                <option value="">Unassigned</option>
                {tenants.filter(t => t.isActive).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
              </select>
              <p className="text-xs text-gray-600 mt-1">Choose the subscription this unit belongs to (super admin only).</p>
            </div>
          )}
          {department && (
            <div className="flex items-center gap-3">
              <label className="text-sm text-gray-500">Active</label>
              <button type="button" onClick={() => setIsActive(!isActive)}
                className={`relative w-10 h-6 rounded-full transition-colors ${isActive ? 'bg-amber-600' : 'bg-gray-700'}`}>
                <div className={`absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white transition-transform ${isActive ? 'translate-x-4' : 'translate-x-0'}`} />
              </button>
            </div>
          )}
          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : department ? 'Save Changes' : 'Create Department'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

// ─── INTEGRATIONS ─────────────────────────────────────────────────────────

function IntegrationsSection() {
  const [syncing, setSyncing] = useState(false)
  const [syncResult, setSyncResult] = useState<{ synced: number; timestamp: string } | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)

  // Integration settings
  const [adInterval, setAdInterval] = useState(15)
  const [calInterval, setCalInterval] = useState(5)
  const [teamsEnabled, setTeamsEnabled] = useState(true)
  const [emailEnabled, setEmailEnabled] = useState(true)
  const [smsEnabled, setSmsEnabled] = useState(true)

  useEffect(() => {
    loadSettings()
  }, [])

  async function loadSettings() {
    try {
      const all = await settingsApi.getAll()
      for (const s of all) {
        switch (s.key) {
          case 'sync.ad_interval_minutes': setAdInterval(Number(s.value) || 15); break
          case 'sync.calendar_interval_minutes': setCalInterval(Number(s.value) || 5); break
          case 'notifications.teams_enabled': setTeamsEnabled(s.value === 'true'); break
          case 'notifications.email_enabled': setEmailEnabled(s.value === 'true'); break
          case 'notifications.sms_escalation_enabled': setSmsEnabled(s.value === 'true'); break
        }
      }
    } catch { /* defaults */ }
    setLoading(false)
  }

  async function handleSyncNow() {
    setSyncing(true)
    setError(null)
    try {
      const result = await integrationsApi.syncAd()
      const timestamp = new Date().toLocaleString()
      setSyncResult({ synced: result.synced, timestamp })
      setSuccess(`AD sync complete: ${result.synced} users processed.`)
      setTimeout(() => setSuccess(null), 4000)
    } catch {
      setError('AD sync failed. Check the Graph API configuration.')
    } finally {
      setSyncing(false)
    }
  }

  async function saveSetting(key: string, value: string) {
    try {
      await settingsApi.upsert(key, value)
      setSuccess('Setting saved.')
      setTimeout(() => setSuccess(null), 3000)
    } catch {
      setError('Failed to save setting.')
    }
  }

  if (loading) return <div className="flex items-center justify-center py-20"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>

  return (
    <div className="space-y-6 max-w-2xl">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}
      {success && (
        <div className="flex items-center gap-3 bg-green-600/10 border border-green-600/30 rounded-xl px-5 py-3 text-sm text-green-400">
          <CheckCircle className="w-5 h-5 flex-shrink-0" /><span>{success}</span>
        </div>
      )}

      {/* M365 Connection Status */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium flex items-center gap-2">
          <Shield className="w-5 h-5 text-amber-500" />
          Microsoft 365 Connection
        </h2>
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-sm">
              <span className="w-2 h-2 rounded-full bg-green-500" />
              Microsoft Entra ID
            </div>
            <span className="text-xs text-green-500">Connected</span>
          </div>
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-sm">
              <span className="w-2 h-2 rounded-full bg-yellow-500" />
              Microsoft Graph API
            </div>
            <span className="text-xs text-yellow-500">Configured</span>
          </div>
        </div>
      </section>

      {/* AD Sync */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium">Active Directory Sync</h2>
        <div className="flex items-center justify-between">
          <div>
            <p className="text-sm text-gray-500">
              Automatically sync users every {adInterval} minutes
            </p>
            {syncResult && (
              <p className="text-xs text-green-500 mt-1">
                Last sync: {syncResult.timestamp} — {syncResult.synced} users processed
              </p>
            )}
          </div>
          <div className="flex items-center gap-3">
            <button
              onClick={handleSyncNow}
              disabled={syncing}
              className="flex items-center gap-1.5 px-3 py-1.5 bg-gray-800 hover:bg-gray-700 disabled:opacity-50 rounded-lg text-xs transition-colors"
            >
              <RefreshCw className={`w-3.5 h-3.5 ${syncing ? 'animate-spin' : ''}`} />
              {syncing ? 'Syncing...' : 'Sync Now'}
            </button>
            <input type="range" min={5} max={60} value={adInterval}
              onChange={e => { setAdInterval(Number(e.target.value)); saveSetting('sync.ad_interval_minutes', e.target.value) }}
              className="w-24" />
          </div>
        </div>
      </section>

      {/* Calendar Sync */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium">Calendar Sync</h2>
        <div className="flex items-center justify-between">
          <div>
            <p className="text-sm text-gray-500">Push on-call shifts to Outlook calendars</p>
            <p className="text-xs text-gray-600 mt-0.5">Syncs every {calInterval} minutes</p>
          </div>
          <div className="flex items-center gap-3">
            <input type="range" min={1} max={30} value={calInterval}
              onChange={e => { setCalInterval(Number(e.target.value)); saveSetting('sync.calendar_interval_minutes', e.target.value) }}
              className="w-24" />
          </div>
        </div>
      </section>

      {/* Notifications */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium">Notifications</h2>
        {([
          { key: 'teams', label: 'Teams Notifications', desc: 'Shift reminders and escalation alerts via Microsoft Teams', val: teamsEnabled, set: setTeamsEnabled, settingKey: 'notifications.teams_enabled' },
          { key: 'email', label: 'Email Notifications', desc: 'Schedule changes and swap approvals via email', val: emailEnabled, set: setEmailEnabled, settingKey: 'notifications.email_enabled' },
          { key: 'sms', label: 'SMS for Escalations', desc: 'Critical escalation alerts via text message', val: smsEnabled, set: setSmsEnabled, settingKey: 'notifications.sms_escalation_enabled' },
        ] as const).map(({ key, label, desc, val, set, settingKey }) => (
          <div key={key} className="flex items-center justify-between">
            <div>
              <p className="text-sm font-medium">{label}</p>
              <p className="text-xs text-gray-500">{desc}</p>
            </div>
            <button
              onClick={() => { set(!val); saveSetting(settingKey, String(!val)) }}
              className={`relative w-10 h-6 rounded-full transition-colors ${val ? 'bg-amber-600' : 'bg-gray-700'}`}
            >
              <div className={`absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white transition-transform ${val ? 'translate-x-4' : 'translate-x-0'}`} />
            </button>
          </div>
        ))}
      </section>
      {/* Code Call Dispatch Integration */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium flex items-center gap-2">
          <Phone className="w-5 h-5 text-red-500" />
          Code Call Dispatch Integration
        </h2>
        <p className="text-xs text-gray-500">
          Dispatch credentials are held in App Service configuration / Key Vault, never in
          the application database. Use these checks to confirm each channel is reachable.
        </p>

        <TwilioDispatchCard />

        <div className="border-t border-gray-800 pt-4 space-y-3">
          <DispatchChannelCheck channel="vocera" label="Vocera Alerts" />
          <DispatchChannelCheck channel="informacast" label="InformaCast Broadcast" />
          <DispatchChannelCheck channel="cucm" label="Cisco CUCM Paging" />
        </div>
      </section>
    </div>
  )
}

/**
 * Connection check for a dispatch channel. Credentials are configured out-of-band
 * (Dispatch__* app settings), so this only reports reachability.
 */
function DispatchChannelCheck({ channel, label }: {
  channel: 'twilio' | 'vocera' | 'informacast' | 'cucm'; label: string
}) {
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState<ConnectionStatus | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function handleTest() {
    setTesting(true)
    setError(null)
    setResult(null)
    try {
      setResult(await integrationsApi.testDispatchChannel(channel))
    } catch {
      setError('Check failed — the API could not reach this channel.')
    }
    setTesting(false)
  }

  return (
    <div className="flex items-center justify-between gap-3">
      <div className="min-w-0">
        <p className="text-sm">{label}</p>
        {result && (
          <p className={`text-xs mt-0.5 ${result.connected ? 'text-green-500' : 'text-red-400'}`}>
            {result.detail}
          </p>
        )}
        {error && <p className="text-xs text-red-400 mt-0.5">{error}</p>}
      </div>
      <button
        onClick={handleTest}
        disabled={testing}
        className="shrink-0 px-3 py-1.5 bg-gray-800 hover:bg-gray-700 disabled:opacity-50 rounded-lg text-xs transition-colors"
      >
        {testing ? 'Checking...' : 'Test connection'}
      </button>
    </div>
  )
}

/** Twilio SMS: connection check plus a test send, so the channel can be proven end-to-end. */
function TwilioDispatchCard() {
  const [phone, setPhone] = useState('')
  const [sending, setSending] = useState(false)
  const [sendResult, setSendResult] = useState<{ ok: boolean; text: string } | null>(null)

  async function handleSend() {
    if (!phone.trim()) return
    setSending(true)
    setSendResult(null)
    try {
      const res = await integrationsApi.sendTestSms(phone.trim())
      setSendResult({
        ok: res.sent,
        text: res.sent
          ? `Test message accepted by Twilio${res.messageSid ? ` (${res.messageSid})` : ''}. Check the handset.`
          : res.detail || 'Twilio rejected the message.',
      })
    } catch {
      setSendResult({ ok: false, text: 'Send failed — check that Twilio is enabled and configured.' })
    }
    setSending(false)
  }

  return (
    <div className="space-y-3">
      <DispatchChannelCheck channel="twilio" label="Twilio SMS to on-call provider" />
      <div>
        <label className="block text-xs text-gray-500 mb-1">Send a test SMS</label>
        <div className="flex items-center gap-2">
          <input
            type="tel"
            value={phone}
            onChange={e => setPhone(e.target.value)}
            placeholder="+12025551234"
            className="flex-1 bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 font-mono"
          />
          <button
            onClick={handleSend}
            disabled={sending || !phone.trim()}
            className="shrink-0 px-3 py-2 bg-gray-800 hover:bg-gray-700 disabled:opacity-50 rounded-lg text-xs transition-colors"
          >
            {sending ? 'Sending...' : 'Send test'}
          </button>
        </div>
        {sendResult && (
          <p className={`text-xs mt-1.5 ${sendResult.ok ? 'text-green-500' : 'text-red-400'}`}>
            {sendResult.text}
          </p>
        )}
        <p className="text-xs text-gray-600 mt-1.5">
          Sends a message marked <span className="font-mono">[TEST]</span> from the hospital's
          Twilio number. Recorded in the audit log.
        </p>
      </div>
    </div>
  )
}

// ─── TIME OFF APPROVAL ────────────────────────────────────────────────────

function TimeOffApprovalSection() {
  const [requests, setRequests] = useState<TimeOff[]>([])
  const [loading, setLoading] = useState(true)
  const [filter, setFilter] = useState<string>('pending')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    loadRequests()
  }, [filter])

  async function loadRequests() {
    try {
      setLoading(true)
      const data = await scheduleApi.getAllTimeOff(filter || undefined)
      setRequests(data)
    } catch {
      setError('Failed to load time-off requests.')
    } finally {
      setLoading(false)
    }
  }

  async function handleApprove(req: TimeOff) {
    const who = req.employee ? `${req.employee.firstName} ${req.employee.lastName}` : 'this request'
    const reason = window.prompt(`${who} — approve (optional reason):`, '') ?? ''
    try {
      setError(null)
      await scheduleApi.approveTimeOff(req.id, reason || undefined)
      await loadRequests()
    } catch {
      setError('Failed to approve request.')
    }
  }

  async function handleDeny(req: TimeOff) {
    const who = req.employee ? `${req.employee.firstName} ${req.employee.lastName}` : 'this request'
    const reason = window.prompt(`${who} — deny (optional reason):`, '') ?? ''
    try {
      setError(null)
      await scheduleApi.denyTimeOff(req.id, reason || undefined)
      await loadRequests()
    } catch {
      setError('Failed to deny request.')
    }
  }

  const typeLabels: Record<string, string> = {
    pto: 'PTO', cme: 'CME', holiday: 'Holiday', sick: 'Sick',
    personal: 'Personal', bereavement: 'Bereavement', military: 'Military',
    jury_duty: 'Jury Duty', unpaid: 'Unpaid',
  }

  if (loading) return <div className="flex items-center justify-center py-20"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>

  return (
    <div className="space-y-4">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}

      {/* Filter tabs */}
      <div className="flex gap-1 bg-gray-900 border border-gray-800 rounded-xl p-1 w-fit">
        {['pending', 'approved', 'denied', ''].map(s => (
          <button
            key={s || 'all'}
            onClick={() => setFilter(s)}
            className={`px-4 py-1.5 rounded-lg text-xs font-medium transition-colors ${
              filter === s ? 'bg-amber-600/20 text-amber-500' : 'text-gray-400 hover:text-gray-200'
            }`}
          >
            {s || 'All'}
          </button>
        ))}
      </div>

      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800">
          <p className="text-sm text-gray-500">{requests.length} request{requests.length !== 1 ? 's' : ''}</p>
        </div>
        {requests.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <Calendar className="w-12 h-12 mb-4 text-gray-700" />
            <p>No time-off requests found</p>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {requests.map(req => (
              <div key={req.id} className="px-5 py-4 flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium">
                    {req.employee?.firstName} {req.employee?.lastName}
                    <span className="text-xs text-gray-500 ml-2 font-normal">{typeLabels[req.type] || req.type}</span>
                  </p>
                  <p className="text-xs text-gray-500 mt-0.5">
                    {formatDateOnly(req.startDate)} — {formatDateOnly(req.endDate)}
                  </p>
                  {req.notes && <p className="text-xs text-gray-600 mt-1">{req.notes}</p>}
                  {req.status !== 'pending' && (
                    <p className="text-xs text-gray-600 mt-1">
                      {req.status === 'approved' ? 'Approved' : 'Denied'}
                      {req.approvedBy ? ` by ${req.approvedBy.firstName} ${req.approvedBy.lastName}` : ''}
                      {req.approvalReason ? ` — "${req.approvalReason}"` : ''}
                    </p>
                  )}
                </div>
                <div className="flex items-center gap-2">
                  {req.status === 'pending' && (
                    <>
                      <button
                        onClick={() => handleApprove(req)}
                        className="flex items-center gap-1 px-3 py-1.5 bg-green-600/20 hover:bg-green-600/30 text-green-500 rounded-lg text-xs transition-colors"
                      >
                        <CheckCircle className="w-3.5 h-3.5" /> Approve
                      </button>
                      <button
                        onClick={() => handleDeny(req)}
                        className="flex items-center gap-1 px-3 py-1.5 bg-red-600/20 hover:bg-red-600/30 text-red-500 rounded-lg text-xs transition-colors"
                      >
                        <X className="w-3.5 h-3.5" /> Deny
                      </button>
                    </>
                  )}
                  <span className={`text-xs px-2 py-0.5 rounded-full ${
                    req.status === 'approved' ? 'bg-green-600/20 text-green-500' :
                    req.status === 'denied' ? 'bg-red-600/20 text-red-500' :
                    'bg-yellow-600/20 text-yellow-500'
                  }`}>
                    {req.status}
                  </span>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}

// ─── TENANTS (Business/Facility Management) ────────────────────────────────

function TenantsSection({ setActiveTenantId }: { setActiveTenantId: (id: number | null) => void }) {
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [admins, setAdmins] = useState<Record<number, TenantAdmin[]>>({})
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [showModal, setShowModal] = useState(false)
  const [editingTenant, setEditingTenant] = useState<Tenant | null>(null)
  const [showAdminModal, setShowAdminModal] = useState(false)
  const [adminTenantId, setAdminTenantId] = useState<number | null>(null)
  const [expandedTenant, setExpandedTenant] = useState<number | null>(null)
  const [showNewSubscription, setShowNewSubscription] = useState(false)

  useEffect(() => { loadData() }, [])

  async function loadData() {
    setLoading(true)
    try {
      const tenantsData = await tenantsApi.getAll(true)
      setTenants(tenantsData)
      const adminMap: Record<number, TenantAdmin[]> = {}
      await Promise.all(tenantsData.map(async t => {
        try {
          adminMap[t.id] = await tenantsApi.getAdmins(t.id)
        } catch { adminMap[t.id] = [] }
      }))
      setAdmins(adminMap)
    } catch { setError('Failed to load tenants.') }
    setLoading(false)
  }

  async function handleSave(data: Partial<Tenant>) {
    try {
      setError(null)
      if (editingTenant) {
        const updated = await tenantsApi.update(editingTenant.id, data as Record<string, unknown>)
        setTenants(prev => prev.map(t => t.id === updated.id ? updated : t))
      } else {
        const created = await tenantsApi.create(data as Record<string, unknown>)
        setTenants(prev => [...prev, created])
      }
      setShowModal(false)
      setEditingTenant(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save tenant.')
    }
  }

  async function handleDeactivate(tenant: Tenant) {
    try {
      setError(null)
      await tenantsApi.deactivate(tenant.id)
      setTenants(prev => prev.map(t => t.id === tenant.id ? { ...t, isActive: false } : t))
    } catch { setError('Failed to deactivate tenant.') }
  }

  async function handleAssignAdmin(azureAdObjectId: string, role: string) {
    if (!adminTenantId) return
    try {
      setError(null)
      const admin = await tenantsApi.assignAdmin(adminTenantId, { azureAdObjectId, role })
      setAdmins(prev => ({
        ...prev,
        [adminTenantId]: [...(prev[adminTenantId] || []), admin],
      }))
      setShowAdminModal(false)
      setAdminTenantId(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to assign admin.')
    }
  }

  async function handleRemoveAdmin(tenantId: number, adminId: number) {
    try {
      setError(null)
      await tenantsApi.removeAdmin(tenantId, adminId)
      setAdmins(prev => ({
        ...prev,
        [tenantId]: (prev[tenantId] || []).filter(a => a.id !== adminId),
      }))
    } catch { setError('Failed to remove admin.') }
  }

  if (loading) return <div className="flex items-center justify-center py-20"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>

  return (
    <div className="space-y-4">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-500">{tenants.length} tenant{tenants.length !== 1 ? 's' : ''}</p>
        <div className="flex items-center gap-2">
          <button
            onClick={() => setShowNewSubscription(true)}
            className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
          >
            <Plus className="w-4 h-4" /> Onboard Subscription
          </button>
          <button
            onClick={() => { setEditingTenant(null); setShowModal(true) }}
            className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm font-medium transition-colors"
          >
            <Building className="w-4 h-4" /> Add Tenant
          </button>
        </div>
      </div>

      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        {tenants.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <Building className="w-12 h-12 mb-4 text-gray-700" />
            <p>No tenants configured</p>
            <button
              onClick={() => { setEditingTenant(null); setShowModal(true) }}
              className="mt-4 flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Plus className="w-4 h-4" /> Add Tenant
            </button>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {tenants.map(tenant => (
              <div key={tenant.id}>
                <div className="px-5 py-4 flex items-center justify-between group">
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <p className="text-sm font-medium">{tenant.name}</p>
                      {!tenant.isActive && (
                        <span className="text-xs px-2 py-0.5 rounded-full bg-red-600/20 text-red-500">Inactive</span>
                      )}
                    </div>
                    <p className="text-xs text-gray-500 mt-0.5">
                      {tenant.description || 'No description'}
                      {tenant.contactEmail && ` · ${tenant.contactEmail}`}
                    </p>
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => setExpandedTenant(expandedTenant === tenant.id ? null : tenant.id)}
                      className="text-xs text-gray-500 hover:text-gray-300 transition-colors"
                    >
                      {admins[tenant.id]?.length || 0} Admin{(admins[tenant.id]?.length || 0) !== 1 ? 's' : ''}
                    </button>
                    <button
                      onClick={() => { setAdminTenantId(tenant.id); setShowAdminModal(true) }}
                      className="text-xs px-2 py-1 rounded bg-gray-800 hover:bg-gray-700 text-gray-400 hover:text-gray-200 transition-colors"
                    >
                      + Admin
                    </button>
                    <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                      <button
                        onClick={() => { setEditingTenant(tenant); setShowModal(true) }}
                        className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors" title="Edit"
                      >
                        <svg className="w-4 h-4 text-gray-400 hover:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                        </svg>
                      </button>
                      {tenant.isActive && (
                        <button
                          onClick={() => handleDeactivate(tenant)}
                          className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors" title="Deactivate"
                        >
                          <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                        </button>
                      )}
                    </div>
                  </div>
                </div>

                {/* Expanded admin list */}
                {expandedTenant === tenant.id && (
                  <div className="px-5 pb-4 border-t border-gray-800">
                    <div className="mt-3 space-y-2">
                      <p className="text-xs text-gray-500 mb-2">Assigned Admins</p>
                      {(!admins[tenant.id] || admins[tenant.id].length === 0) ? (
                        <p className="text-xs text-gray-600">No admins assigned.</p>
                      ) : (
                        admins[tenant.id].map(admin => (
                          <div key={admin.id} className="flex items-center justify-between py-1.5 px-3 bg-gray-800/50 rounded-lg">
                            <div className="flex items-center gap-2">
                              <span className="text-xs font-mono text-gray-400">{admin.azureAdObjectId}</span>
                              <span className={`text-xs px-1.5 py-0.5 rounded ${
                                admin.role === 'SuperAdmin' ? 'bg-purple-600/20 text-purple-500' : 'bg-blue-600/20 text-blue-500'
                              }`}>
                                {admin.role}
                              </span>
                              {admin.isAutoAssigned && (
                                <span className="text-xs text-gray-600">(auto)</span>
                              )}
                            </div>
                            <button
                              onClick={() => handleRemoveAdmin(tenant.id, admin.id)}
                              className="text-xs text-red-500 hover:text-red-400 transition-colors"
                            >
                              Remove
                            </button>
                          </div>
                        ))
                      )}
                    </div>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Tenant Form Modal */}
      {showModal && (
        <TenantFormModal
          tenant={editingTenant}
          onSave={handleSave}
          onClose={() => { setShowModal(false); setEditingTenant(null) }}
        />
      )}

      {/* Assign Admin Modal */}
      {showAdminModal && adminTenantId && (
        <AssignAdminModal
          tenantName={tenants.find(t => t.id === adminTenantId)?.name ?? `Tenant ${adminTenantId}`}
          onAssign={handleAssignAdmin}
          onClose={() => { setShowAdminModal(false); setAdminTenantId(null) }}
        />
      )}

      {/* Onboard Subscription Modal */}
      {showNewSubscription && (
        <NewSubscriptionModal
          onClose={() => setShowNewSubscription(false)}
          onCreated={(id) => {
            setShowNewSubscription(false)
            setActiveTenantId(id)
            loadData()
          }}
        />
      )}
    </div>
  )
}

// ─── NEW SUBSCRIPTION (guided onboarding) ─────────────────────────────────

function NewSubscriptionModal({ onClose, onCreated }: {
  onClose: () => void
  onCreated: (tenantId: number) => void
}) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [contactEmail, setContactEmail] = useState('')
  const [adminOid, setAdminOid] = useState('')
  const [adminRole, setAdminRole] = useState('DepartmentAdmin')
  const [deptName, setDeptName] = useState('General')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Subscription name is required.'); return }
    setSaving(true)
    setError(null)
    let tenantId: number | null = null
    try {
      // 1) Create the subscription (tenant)
      const tenant = await tenantsApi.create({ name: name.trim(), description: description.trim() || undefined, contactEmail: contactEmail.trim() || undefined } as Record<string, unknown>)
      tenantId = tenant.id
      // 2) Create a default department inside it
      await adminApi.createDepartment({ name: deptName.trim() || 'General', tenantId: tenant.id } as Record<string, unknown>)
      // 3) Optionally assign a tenant admin
      if (adminOid.trim()) {
        await tenantsApi.assignAdmin(tenant.id, { azureAdObjectId: adminOid.trim(), role: adminRole })
      }
      onCreated(tenantId)
    } catch (err) {
      // Compensation: if steps 2/3 failed, roll back the just-created subscription so we
      // don't leave an orphaned tenant the user can't see.
      if (tenantId != null) {
        try { await tenantsApi.deactivate(tenantId) } catch { /* best effort */ }
      }
      setError(err instanceof Error ? err.message : 'Failed to create the subscription.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Onboard New Subscription</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          <p className="text-xs text-gray-500">Creates the subscription, a default department, and (optionally) a tenant admin. You can then import or sync users into it.</p>
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Subscription name *</label>
            <input type="text" required value={name} onChange={e => setName(e.target.value)} placeholder="e.g. St. Mary's — West Wing"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Description</label>
            <textarea value={description} onChange={e => setDescription(e.target.value)} rows={2}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none" />
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Default department</label>
              <input type="text" value={deptName} onChange={e => setDeptName(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Contact email</label>
              <input type="email" value={contactEmail} onChange={e => setContactEmail(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
          </div>
          <div className="border-t border-gray-800 pt-4 space-y-3">
            <p className="text-xs text-gray-500">Optional — assign this subscription's first administrator:</p>
            <div className="grid grid-cols-2 gap-4">
              <div>
                <label className="block text-sm text-gray-500 mb-1">Their Entra object id</label>
                <input type="text" value={adminOid} onChange={e => setAdminOid(e.target.value)} placeholder="oid (optional)"
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 font-mono" />
              </div>
              <div>
                <label className="block text-sm text-gray-500 mb-1">Role</label>
                <select value={adminRole} onChange={e => setAdminRole(e.target.value)}
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
                  <option value="DepartmentAdmin">Department Admin</option>
                  <option value="SuperAdmin">Scoped Super Admin (this subscription only)</option>
                </select>
              </div>
            </div>
          </div>
          <div className="flex justify-end gap-2 pt-2 border-t border-gray-800">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Plus className="w-4 h-4" />}
              {saving ? 'Creating...' : 'Create Subscription'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

function TenantFormModal({ tenant, onSave, onClose }: {
  tenant: Tenant | null
  onSave: (data: Partial<Tenant>) => Promise<void>
  onClose: () => void
}) {
  const [name, setName] = useState(tenant?.name || '')
  const [description, setDescription] = useState(tenant?.description || '')
  const [azureAdGroupId, setAzureAdGroupId] = useState(tenant?.azureAdGroupId || '')
  const [contactEmail, setContactEmail] = useState(tenant?.contactEmail || '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Tenant name is required.'); return }
    setSaving(true)
    setError(null)
    try {
      await onSave({
        name: name.trim(),
        description: description.trim() || undefined,
        azureAdGroupId: azureAdGroupId.trim() || undefined,
        contactEmail: contactEmail.trim() || undefined,
      } as Partial<Tenant>)
    } catch { setError('Failed to save tenant.') }
    finally { setSaving(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{tenant ? 'Edit Tenant' : 'Add Tenant'}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Name *</label>
            <input type="text" required value={name} onChange={e => setName(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Description</label>
            <textarea value={description} onChange={e => setDescription(e.target.value)} rows={3}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Azure AD Group ID</label>
            <input type="text" value={azureAdGroupId} onChange={e => setAzureAdGroupId(e.target.value)}
              placeholder="For auto-assigning sub-admins via group membership"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 font-mono" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Contact Email</label>
            <input type="email" value={contactEmail} onChange={e => setContactEmail(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : tenant ? 'Save Changes' : 'Create Tenant'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

function AssignAdminModal({ tenantName, onAssign, onClose }: {
  tenantName: string
  onAssign: (azureAdObjectId: string, role: string) => Promise<void>
  onClose: () => void
}) {
  const [azureAdObjectId, setAzureAdObjectId] = useState('')
  const [role, setRole] = useState('DepartmentAdmin')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!azureAdObjectId.trim()) { setError('Azure AD Object ID is required.'); return }
    setSaving(true)
    setError(null)
    try {
      await onAssign(azureAdObjectId.trim(), role)
    } catch { setError('Failed to assign admin.') }
    finally { setSaving(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Assign Admin — {tenantName}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Azure AD Object ID *</label>
            <input type="text" required value={azureAdObjectId} onChange={e => setAzureAdObjectId(e.target.value)}
              placeholder="User's Azure AD Object ID (oid claim)"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 font-mono" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Role</label>
            <select value={role} onChange={e => setRole(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
              <option value="DepartmentAdmin">Department Admin (scoped to tenant)</option>
              <option value="SuperAdmin">Scoped Super Admin (this subscription only)</option>
            </select>
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : 'Assign Admin'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
