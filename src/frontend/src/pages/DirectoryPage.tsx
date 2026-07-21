import { useState, useEffect } from 'react'
import { Search, Phone, Mail, MapPin, ShieldCheck, Upload, MessageSquare } from 'lucide-react'
import { directoryApi, importApi } from '@/services/api'
import ImportModal from '@/components/ImportModal'
import type { Employee } from '@/types'

export default function DirectoryPage() {
  const [query, setQuery] = useState('')
  const [employees, setEmployees] = useState<Employee[]>([])
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const [loading, setLoading] = useState(true)
  const [showImport, setShowImport] = useState(false)

  useEffect(() => {
    directoryApi
      .search('')
      .then((data) => {
        setEmployees(data)
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
        <button
          onClick={() => setShowImport(true)}
          className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm transition-colors"
        >
          <Upload className="w-4 h-4" />
          Import CSV
        </button>
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
                  </div>
                )}
                {selectedEmployee.mobilePhone && (
                  <div className="flex items-center gap-3 text-sm">
                    <Phone className="w-4 h-4 text-gray-500" />
                    <span>{selectedEmployee.mobilePhone}</span>
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
        description="Upload a CSV file with employee data. Columns: azureAdObjectId, firstName, lastName, email, title, officePhone, mobilePhone, officeLocation, departmentId"
        onValidate={(file) => importApi.validateEmployees(file)}
        onImport={(file) => importApi.importEmployees(file)}
      />
    </div>
  )
}
