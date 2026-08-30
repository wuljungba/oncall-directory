import { useState, useEffect } from 'react'
import { Plus, Save, X, Trash2, AlertTriangle, MapPin } from 'lucide-react'
import { codeCallLocationsApi, departmentsApi } from '@/services/api'
import type { CodeCallLocation, Department } from '@/types'

// ─── CODE CALL LOCATIONS ─────────────────────────────────────────────────

export default function CodeCallLocationsSection() {
  const [locations, setLocations] = useState<CodeCallLocation[]>([])
  const [loading, setLoading] = useState(true)
  const [showModal, setShowModal] = useState(false)
  const [editingLocation, setEditingLocation] = useState<CodeCallLocation | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { loadLocations() }, [])

  async function loadLocations() {
    try {
      setLocations(await codeCallLocationsApi.getAll(true))
    } catch { setError('Failed to load locations.') }
    setLoading(false)
  }

  async function handleSave(data: Partial<CodeCallLocation>) {
    try {
      setError(null)
      if (editingLocation) {
        const updated = await codeCallLocationsApi.update(editingLocation.id, data)
        setLocations(prev => prev.map(l => l.id === updated.id ? updated : l))
      } else {
        const created = await codeCallLocationsApi.create(data)
        setLocations(prev => [...prev, created])
      }
      setShowModal(false)
      setEditingLocation(null)
    } catch {
      // The server answers 404 when the department is missing or belongs to another
      // tenant, which as a bare "failed to save" sent people looking at the name field.
      setError('Could not save the location. Choose a department you administer — a location without one would be invisible to you afterwards.')
    }
  }

  async function handleDelete(id: number) {
    try {
      setError(null)
      await codeCallLocationsApi.delete(id)
      setLocations(prev => prev.map(l => l.id === id ? { ...l, isActive: false } : l))
    } catch { setError('Failed to deactivate location.') }
  }

  if (loading) return <div className="flex items-center justify-center py-20"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>

  return (
    <div className="space-y-4">
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}

      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-500">{locations.length} location{locations.length !== 1 ? 's' : ''}</p>
        <button
          onClick={() => { setEditingLocation(null); setShowModal(true) }}
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
        >
          <Plus className="w-4 h-4" /> Add Location
        </button>
      </div>

      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        {locations.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <MapPin className="w-12 h-12 mb-4 text-gray-700" />
            <p>No locations configured</p>
            <p className="text-xs mt-1 text-gray-600">Add locations for code call dispatch targeting</p>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {locations.map(loc => (
              <div key={loc.id} className="px-5 py-4 flex items-center justify-between group">
                <div>
                  <p className="text-sm font-medium">{loc.name}</p>
                  <p className="text-xs text-gray-500 mt-0.5">
                    {loc.zone ? `Zone: ${loc.zone}` : 'No zone'}
                    {loc.department ? ` · ${loc.department.name}` : ''}
                  </p>
                </div>
                <div className="flex items-center gap-3">
                  {!loc.isActive && (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-red-600/20 text-red-500">Inactive</span>
                  )}
                  <button
                    onClick={() => { setEditingLocation(loc); setShowModal(true) }}
                    className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors opacity-0 group-hover:opacity-100"
                    title="Edit"
                  >
                    <svg className="w-4 h-4 text-gray-400 hover:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                    </svg>
                  </button>
                  <button
                    onClick={() => handleDelete(loc.id)}
                    className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors opacity-0 group-hover:opacity-100"
                    title="Deactivate"
                  >
                    <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {showModal && (
        <LocationFormModal
          location={editingLocation}
          onSave={handleSave}
          onClose={() => { setShowModal(false); setEditingLocation(null) }}
        />
      )}
    </div>
  )
}

function LocationFormModal({ location, onSave, onClose }: {
  location: CodeCallLocation | null
  onSave: (data: Partial<CodeCallLocation>) => Promise<void>
  onClose: () => void
}) {
  const [name, setName] = useState(location?.name || '')
  const [zone, setZone] = useState(location?.zone || '')
  const [departmentId, setDepartmentId] = useState<number | ''>(location?.departmentId ?? '')
  const [departments, setDepartments] = useState<Department[]>([])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // The list is already scoped to the caller's tenants server-side, so whatever comes
  // back is exactly the set this user is allowed to file a location against.
  useEffect(() => {
    let cancelled = false
    departmentsApi.getAll()
      .then(d => { if (!cancelled) setDepartments(d) })
      .catch(() => { /* the field stays empty and the required check below explains why */ })
    return () => { cancelled = true }
  }, [])

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Location name is required.'); return }
    if (departmentId === '') {
      setError(departments.length === 0
        ? 'No departments are available to you, so a location cannot be filed yet.'
        : 'Choose a department. A code call location has to belong to one.')
      return
    }
    setSaving(true)
    setError(null)
    try {
      await onSave({
        name: name.trim(),
        zone: zone.trim() || undefined,
        departmentId: Number(departmentId),
        isActive: location?.isActive ?? true,
      })
    } catch { setError('Failed to save.') }
    finally { setSaving(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{location ? 'Edit Location' : 'Add Location'}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 shrink-0" />{error}
            </div>
          )}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Name *</label>
            <input type="text" required value={name} onChange={e => setName(e.target.value)}
              placeholder="e.g., 3 West — Room 312"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Department *</label>
            <select value={departmentId}
              onChange={e => setDepartmentId(e.target.value === '' ? '' : Number(e.target.value))}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
              <option value="">Select a department</option>
              {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Zone (for SIP paging, optional)</label>
            <input type="text" value={zone} onChange={e => setZone(e.target.value)}
              placeholder="e.g., 3-west"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>
          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : 'Save'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
