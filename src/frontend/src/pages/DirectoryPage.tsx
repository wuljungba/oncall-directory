import { useState, useEffect } from 'react'
import { Search, Phone, Mail, MapPin, ShieldCheck, Upload, Download, MessageSquare, AlertTriangle, X, Save, Pencil, Plus } from 'lucide-react'
import { directoryApi, importApi, adminApi, departmentsApi, tenantsApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import ImportModal from '@/components/ImportModal'
import { isValidE164 } from '@/utils/validation'
import type { Employee, Department, Tenant } from '@/types'

export default function DirectoryPage() {
  const { canDirectoryWrite, isAdmin, canTenantManage, activeTenantId } = useAuth()
  const canPickTenant = isAdmin || canTenantManage
  const [tenants, setTenants] = useState<Tenant[]>([])
  const [importTenantId, setImportTenantId] = useState<number | ''>(activeTenantId ?? '')
  const [query, setQuery] = useState('')
  const [employees, setEmployees] = useState<Employee[]>([])
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const [loading, setLoading] = useState(true)
  const [showImport, setShowImport] = useState(false)
  const [departments, setDepartments] = useState<Department[]>([])
  const [showEditModal, setShowEditModal] = useState(false)
  const [editingEmployee, setEditingEmployee] = useState<Employee | null>(null)
  const [showAddModal, setShowAddModal] = useState(false)

  useEffect(() => {
    Promise.all([
      directoryApi.search(''),
      departmentsApi.getAll(),
      canPickTenant ? tenantsApi.getAll(true) : Promise.resolve([]),
    ])
      .then(([emps, depts, tnts]) => {
        setEmployees(emps)
        setDepartments(depts)
        setTenants(tnts as Tenant[])
        setLoading(false)
      })
      .catch(() => setLoading(false))
  }, [])

  const handleSearch = async (q: string) => {
    setQuery(q)
    setLoading(true)
    try {
      const data = await directoryApi.search(q)
      setEmployees(data)
    } catch {
      // ignore
    }
    setLoading(false)
  }

  function handleDownloadTemplate() {
    const headers = [
      'azureAdObjectId', 'firstName', 'lastName', 'email', 'title',
      'officePhone', 'mobilePhone', 'officeLocation', 'departmentId',
    ]
    const sampleRow = [
      '', 'Jane', 'Smith', 'jane.smith@hospital.org', 'Attending Physician',
      '+12025551234', '+12025555678', 'Floor 3 - West Wing', '1',
    ]

    const escape = (val: string) => {
      if (val.includes(',') || val.includes('"') || val.includes('\n')) {
        return `"${val.replace(/"/g, '""')}"`
      }
      return val
    }

    const csv = [
      headers.join(','),
      sampleRow.map(escape).join(','),
    ].join('\n')

    const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'employee-import-template.csv'
    a.click()
    URL.revokeObjectURL(url)
  }

  async function handleUpdateEmployee(id: string, data: Partial<Employee>) {
    try {
      const updated = await adminApi.updateEmployee(id, data as Record<string, unknown>)
      setEmployees(prev => prev.map(e => e.id === id ? updated : e))
      setSelectedEmployee(updated)
      setShowEditModal(false)
      setEditingEmployee(null)
    } catch (err) {
      console.error('Failed to update employee:', err)
    }
  }

  async function handleCreateEmployee(data: Partial<Employee>) {
    try {
      const created = await adminApi.createEmployee(data as Record<string, unknown>)
      setEmployees(prev => [...prev, created])
      setSelectedEmployee(created)
      setShowAddModal(false)
    } catch (err) {
      console.error('Failed to create employee:', err)
    }
  }

  const presenceColor = (presence: string) => {
    switch (presence) {
      case 'available': return 'bg-green-500'
      case 'busy': return 'bg-red-500'
      case 'dnd': return 'bg-red-500'
      case 'offline': return 'bg-gray-500'
      default: return 'bg-gray-600'
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Phone Directory</h1>
        <div className="flex items-center gap-2">
          <button
            onClick={handleDownloadTemplate}
            className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm transition-colors"
          >
            <Download className="w-4 h-4" />
            Download Template
          </button>
          {canDirectoryWrite && (
            <button
              onClick={() => setShowAddModal(true)}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Plus className="w-4 h-4" />
              Add Employee
            </button>
          )}
          {canDirectoryWrite && (
            <button
              onClick={() => setShowImport(true)}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Upload className="w-4 h-4" />
              Import CSV
            </button>
          )}
        </div>
      </div>

      {/* Search */}
      <div className="relative">
        <Search className="absolute left-4 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-500" />
        <input
          type="text"
          placeholder="Search by name, title, department, or email..."
          value={query}
          onChange={(e) => handleSearch(e.target.value)}
          className="w-full bg-gray-900 border border-gray-700 rounded-xl pl-11 pr-4 py-3 text-sm focus:outline-none focus:border-amber-600 transition-colors"
        />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Employee List */}
        <div className="lg:col-span-2 bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800">
            <p className="text-sm text-gray-500">
              {employees.length} employee{employees.length !== 1 ? 's' : ''}
            </p>
          </div>
          <div className="divide-y divide-gray-800 max-h-[600px] overflow-y-auto">
            {loading ? (
              <div className="flex items-center justify-center py-12">
                <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" />
              </div>
            ) : employees.length === 0 ? (
              <div className="text-center py-12 text-gray-500">
                <p>No employees found</p>
              </div>
            ) : (
              employees.map((emp) => (
                <button
                  key={emp.id}
                  onClick={() => setSelectedEmployee(emp)}
                  className={`w-full text-left px-5 py-4 hover:bg-gray-800/50 transition-colors ${
                    selectedEmployee?.id === emp.id ? 'bg-gray-800' : ''
                  }`}
                >
                  <div className="flex items-center gap-3">
                    <div className="relative">
                      <div className="w-10 h-10 rounded-full bg-gray-700 flex items-center justify-center text-sm font-medium">
                        {emp.firstName?.charAt(0)}
                        {emp.lastName?.charAt(0)}
                      </div>
                      <div
                        className={`absolute -bottom-0.5 -right-0.5 w-3 h-3 rounded-full border-2 border-gray-900 ${presenceColor(emp.presence)}`}
                      />
                    </div>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-medium truncate">
                        {emp.firstName} {emp.lastName}
                      </p>
                      <p className="text-xs text-gray-500 truncate">
                        {emp.title} {emp.department ? `· ${emp.department.name}` : ''}
                      </p>
                    </div>
                    {emp.onCallStatus && (
                      <span className="text-xs px-2 py-0.5 rounded-full bg-amber-600/20 text-amber-500">
                        On Call
                      </span>
                    )}
                  </div>
                </button>
              ))
            )}
          </div>
        </div>

        {/* Employee Detail */}
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          {selectedEmployee ? (
            <div className="space-y-5">
              <div className="text-center">
                <div className="w-16 h-16 rounded-full bg-gray-700 flex items-center justify-center text-xl font-medium mx-auto">
                  {selectedEmployee.firstName?.charAt(0)}
                  {selectedEmployee.lastName?.charAt(0)}
                </div>
                <h2 className="text-lg font-medium mt-3">
                  {selectedEmployee.firstName} {selectedEmployee.lastName}
                </h2>
                <p className="text-sm text-gray-500">{selectedEmployee.title}</p>
                {selectedEmployee.onCallStatus && (
                  <span className="inline-block mt-2 text-xs px-2 py-0.5 rounded-full bg-amber-600/20 text-amber-500">
                    Currently On Call
                  </span>
                )}
              </div>

              <div className="space-y-3 pt-3 border-t border-gray-800">
                {selectedEmployee.officePhone && (
                  <div className="flex items-center gap-3 text-sm">
                    <Phone className="w-4 h-4 text-gray-500" />
                    <span>{selectedEmployee.officePhone}</span>
                    {!isValidE164(selectedEmployee.officePhone) && (
                      <span className="flex items-center gap-1 text-xs text-yellow-500" title="Not in E.164 format">
                        <AlertTriangle className="w-3 h-3" /> Invalid format
                      </span>
                    )}
                    <span className="text-xs text-gray-600">(office)</span>
                  </div>
                )}
                {selectedEmployee.mobilePhone && (
                  <div className="flex items-center gap-3 text-sm">
                    <Phone className="w-4 h-4 text-gray-500" />
                    <span>{selectedEmployee.mobilePhone}</span>
                    {!isValidE164(selectedEmployee.mobilePhone) && (
                      <span className="flex items-center gap-1 text-xs text-yellow-500" title="Not in E.164 format">
                        <AlertTriangle className="w-3 h-3" /> Invalid format
                      </span>
                    )}
                    <span className="text-xs text-gray-600">(mobile)</span>
                  </div>
                )}
                <div className="flex items-center gap-3 text-sm">
                  <Mail className="w-4 h-4 text-gray-500" />
                  <span>{selectedEmployee.email}</span>
                </div>
                {selectedEmployee.officeLocation && (
                  <div className="flex items-center gap-3 text-sm">
                    <MapPin className="w-4 h-4 text-gray-500" />
                    <span>{selectedEmployee.officeLocation}</span>
                  </div>
                )}
                {selectedEmployee.department && (
                  <div className="flex items-center gap-3 text-sm">
                    <ShieldCheck className="w-4 h-4 text-gray-500" />
                    <span>{selectedEmployee.department.name}</span>
                  </div>
                )}
              </div>

              <div className="flex gap-2">
                {canDirectoryWrite && (
                  <button
                    onClick={() => {
                      setEditingEmployee(selectedEmployee)
                      setShowEditModal(true)
                    }}
                    className="flex-1 text-center px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm font-medium transition-colors"
                  >
                    <Pencil className="w-4 h-4 inline-block mr-1" />
                    Edit
                  </button>
                )}
                <a
                  href={`tel:${selectedEmployee.officePhone || selectedEmployee.mobilePhone}`}
                  className="flex-1 text-center px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
                >
                  Call
                </a>
                <a
                  href={`mailto:${selectedEmployee.email}`}
                  className="flex-1 text-center px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm font-medium transition-colors"
                >
                  Email
                </a>
                <a
                  href={`https://teams.microsoft.com/l/chat/0/0?users=${encodeURIComponent(selectedEmployee.email)}`}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="flex-1 text-center px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm font-medium transition-colors"
                  title="Chat in Microsoft Teams"
                >
                  <MessageSquare className="w-4 h-4 inline-block" />
                </a>
              </div>
            </div>
          ) : (
            <div className="flex flex-col items-center justify-center py-12 text-gray-500">
              <Phone className="w-12 h-12 mb-4 text-gray-700" />
              <p className="text-sm">Select an employee</p>
              <p className="text-xs mt-1">to view details</p>
            </div>
          )}
        </div>
      </div>

      {/* Import Modal */}
      <ImportModal
        isOpen={showImport}
        onClose={() => setShowImport(false)}
        title="Import Employees"
        description="Upload a CSV file with employee data. Columns: azureAdObjectId (optional, leave blank for manual accounts), firstName, lastName, email, title, officePhone, mobilePhone, officeLocation, departmentId. Phone numbers must be in E.164 format (e.g. +12025551234)."
        extra={canPickTenant ? (
          <div>
            <label className="block text-xs text-gray-400 mb-1">Subscription (tenant) to import into</label>
            <select
              value={importTenantId}
              onChange={e => setImportTenantId(e.target.value ? Number(e.target.value) : '')}
              className="w-full bg-gray-900 border border-gray-700 rounded-lg px-3 py-1.5 text-sm focus:outline-none focus:border-amber-600"
            >
              <option value="">Unassigned</option>
              {tenants.filter(t => t.isActive).map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
          </div>
        ) : undefined}
        onValidate={(file) => importApi.validateEmployees(file)}
        onImport={(file) => importApi.importEmployees(file, importTenantId === '' ? undefined : Number(importTenantId))}
      />

      {/* Add Employee Modal */}
      {showAddModal && (
        <EditEmployeeModal
          employee={null}
          departments={departments}
          onSave={(_, data) => handleCreateEmployee(data)}
          onClose={() => setShowAddModal(false)}
        />
      )}

      {/* Edit Employee Modal */}
      {showEditModal && editingEmployee && (
        <EditEmployeeModal
          employee={editingEmployee}
          departments={departments}
          onSave={handleUpdateEmployee}
          onClose={() => {
            setShowEditModal(false)
            setEditingEmployee(null)
          }}
        />
      )}
    </div>
  )
}

function EditEmployeeModal({
  employee,
  departments,
  onSave,
  onClose,
}: {
  employee: Employee | null
  departments: Department[]
  onSave: (id: string, data: Partial<Employee>) => Promise<void>
  onClose: () => void
}) {
  const isEditing = employee !== null
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
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!firstName.trim() || !lastName.trim()) { setError('First and last name are required.'); return }
    if (!email.trim()) { setError('Email is required.'); return }

    setSaving(true)
    setError(null)
    try {
      const data: Partial<Employee> = {
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        email: email.trim(),
        title: title.trim() || undefined,
        specialty: specialty.trim() || undefined,
        clinicalRole: clinicalRole.trim() || undefined,
        officePhone: officePhone.trim() || undefined,
        mobilePhone: mobilePhone.trim() || undefined,
        officeLocation: officeLocation.trim() || undefined,
        departmentId: departmentId || undefined,
      }
      if (isEditing) {
        await onSave(employee.id, data)
      } else {
        await onSave('', data)
      }
    } catch {
      setError(isEditing ? 'Failed to update employee.' : 'Failed to create employee.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{isEditing ? 'Edit Employee' : 'Add Employee'}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>

        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />
              {error}
            </div>
          )}

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">First Name</label>
              <input
                type="text"
                required
                value={firstName}
                onChange={(e) => setFirstName(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Last Name</label>
              <input
                type="text"
                required
                value={lastName}
                onChange={(e) => setLastName(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Email</label>
            <input
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Title</label>
              <input
                type="text"
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                placeholder="e.g., Attending Physician"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Specialty</label>
              <input
                type="text"
                value={specialty}
                onChange={(e) => setSpecialty(e.target.value)}
                placeholder="e.g., Cardiology"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Clinical Role</label>
              <input
                type="text"
                value={clinicalRole}
                onChange={(e) => setClinicalRole(e.target.value)}
                placeholder="e.g., Resident, Fellow"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Department</label>
              <select
                value={departmentId}
                onChange={(e) => setDepartmentId(e.target.value ? Number(e.target.value) : '')}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              >
                <option value="">None</option>
                {departments.map((d) => (
                  <option key={d.id} value={d.id}>{d.name}</option>
                ))}
              </select>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Office Phone</label>
              <input
                type="tel"
                value={officePhone}
                onChange={(e) => setOfficePhone(e.target.value)}
                placeholder="+12025551234"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Mobile Phone</label>
              <input
                type="tel"
                value={mobilePhone}
                onChange={(e) => setMobilePhone(e.target.value)}
                placeholder="+12025555678"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Office Location</label>
            <input
              type="text"
              value={officeLocation}
              onChange={(e) => setOfficeLocation(e.target.value)}
              placeholder="e.g., Floor 3 - West Wing"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            />
          </div>

          <div className="flex justify-end gap-2 pt-2">
            <button
              type="button"
              onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
            >
              {saving ? (
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
              ) : (
                <Save className="w-4 h-4" />
              )}
              {saving ? 'Saving...' : isEditing ? 'Save Changes' : 'Create Employee'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
