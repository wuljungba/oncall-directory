import { useState, useEffect } from 'react'
import {
  AlertTriangle, Plus, Save, X, Trash2, CheckCircle, Clock, Bell,
} from 'lucide-react'
import { escalationApi, departmentsApi } from '@/services/api'
import type { EscalationPolicy, EscalationEvent, Department } from '@/types'

export default function EscalationPage() {
  const [policies, setPolicies] = useState<EscalationPolicy[]>([])
  const [events, setEvents] = useState<EscalationEvent[]>([])
  const [departments, setDepartments] = useState<Department[]>([])
  const [loading, setLoading] = useState(true)
  const [showCreateModal, setShowCreateModal] = useState(false)
  const [showEditModal, setShowEditModal] = useState(false)
  const [editingPolicy, setEditingPolicy] = useState<EscalationPolicy | null>(null)
  const [tab, setTab] = useState<'policies' | 'events'>('policies')

  useEffect(() => {
    loadData()
  }, [])

  async function loadData() {
    try {
      const [p, e, d] = await Promise.all([
        escalationApi.getPolicies(),
        escalationApi.getEvents(),
        departmentsApi.getAll(),
      ])
      setPolicies(p)
      setEvents(e)
      setDepartments(d)
    } catch { /* ignore */ }
    setLoading(false)
  }

  async function handleCreate(data: Partial<EscalationPolicy>) {
    const created = await escalationApi.createPolicy(data)
    setPolicies(prev => [...prev, created])
    setShowCreateModal(false)
  }

  async function handleUpdate(data: Partial<EscalationPolicy>) {
    if (!editingPolicy) return
    const updated = await escalationApi.updatePolicy(editingPolicy.id, data)
    setPolicies(prev => prev.map(p => p.id === updated.id ? updated : p))
    setEditingPolicy(null)
    setShowEditModal(false)
  }

  async function handleDelete(id: number) {
    await escalationApi.deletePolicy(id)
    setPolicies(prev => prev.filter(p => p.id !== id))
  }

  async function handleAcknowledge(eventId: number) {
    await escalationApi.acknowledgeEvent(eventId)
    setEvents(prev => prev.map(e =>
      e.id === eventId ? { ...e, status: 'resolved' as const, resolvedAt: new Date().toISOString() } : e
    ))
  }

  const pendingEvents = events.filter(e => e.status === 'pending')

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
      </div>
    )
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Escalation Policies</h1>
        <div className="flex items-center gap-3">
          {pendingEvents.length > 0 && (
            <span className="flex items-center gap-1.5 px-3 py-1.5 bg-red-600/10 text-red-400 rounded-lg text-sm">
              <Bell className="w-4 h-4" />
              {pendingEvents.length} active
            </span>
          )}
          <button
            onClick={() => { setEditingPolicy(null); setShowCreateModal(true) }}
            className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
          >
            <Plus className="w-4 h-4" /> New Policy
          </button>
        </div>
      </div>

      {/* Tab bar */}
      <div className="flex gap-1 bg-gray-900 border border-gray-800 rounded-xl p-1 w-fit">
        <button
          onClick={() => setTab('policies')}
          className={`px-4 py-2 rounded-lg text-sm font-medium transition-colors ${
            tab === 'policies' ? 'bg-amber-600/20 text-amber-500' : 'text-gray-400 hover:text-gray-200'
          }`}
        >
          Policies ({policies.length})
        </button>
        <button
          onClick={() => setTab('events')}
          className={`px-4 py-2 rounded-lg text-sm font-medium transition-colors ${
            tab === 'events' ? 'bg-amber-600/20 text-amber-500' : 'text-gray-400 hover:text-gray-200'
          }`}
        >
          Event Log ({events.length})
        </button>
      </div>

      {/* Policies tab */}
      {tab === 'policies' && (
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800">
            <p className="text-sm text-gray-500">
              Configure automatic escalation rules for your departments.
              When an on-call employee doesn't respond within the response window,
              the system escalates through configured tiers.
            </p>
          </div>
          {policies.length === 0 ? (
            <div className="flex flex-col items-center py-16 text-gray-500">
              <AlertTriangle className="w-12 h-12 mb-4 text-gray-700" />
              <p>No escalation policies configured</p>
              <p className="text-sm mt-1">
                Create one to automatically escalate when on-call staff don't respond
              </p>
              <button
                onClick={() => setShowCreateModal(true)}
                className="mt-4 flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
              >
                <Plus className="w-4 h-4" /> Create Policy
              </button>
            </div>
          ) : (
            <div className="divide-y divide-gray-800">
              {policies.map(p => (
                <div key={p.id} className="px-5 py-4 flex items-center justify-between group">
                  <div>
                    <p className="font-medium">{p.name}</p>
                    <p className="text-xs text-gray-500 mt-0.5">
                      {p.escalationTierCount} tier{p.escalationTierCount > 1 ? 's' : ''} ·{' '}
                      {p.maxResponseMinutes}min response ·{' '}
                      {p.notificationChannels} ·{' '}
                      {p.department?.name || 'All Departments'}
                    </p>
                  </div>
                  <div className="flex items-center gap-2 opacity-0 group-hover:opacity-100 transition-opacity">
                    <button
                      onClick={() => { setEditingPolicy(p); setShowEditModal(true) }}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors"
                      title="Edit"
                    >
                      <svg className="w-4 h-4 text-gray-400 hover:text-amber-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
                      </svg>
                    </button>
                    <button
                      onClick={() => handleDelete(p.id)}
                      className="p-1.5 hover:bg-gray-800 rounded-lg transition-colors"
                      title="Delete"
                    >
                      <Trash2 className="w-4 h-4 text-gray-400 hover:text-red-400" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Events tab */}
      {tab === 'events' && (
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between">
            <p className="text-sm text-gray-500">Escalation Events</p>
            <span className="text-xs text-gray-500">
              {pendingEvents.length} pending · {events.length - pendingEvents.length} resolved
            </span>
          </div>
          {events.length === 0 ? (
            <div className="flex flex-col items-center py-16 text-gray-500">
              <CheckCircle className="w-12 h-12 mb-4 text-green-600/50" />
              <p>No escalation events recorded</p>
              <p className="text-sm mt-1">Events appear here when an on-call shift misses its response window</p>
            </div>
          ) : (
            <div className="divide-y divide-gray-800 max-h-[500px] overflow-y-auto">
              {events.map(e => (
                <div key={e.id} className={`px-5 py-4 ${e.status === 'pending' ? 'bg-red-600/5' : ''}`}>
                  <div className="flex items-start justify-between gap-4">
                    <div className="flex items-start gap-3 min-w-0">
                      {e.status === 'pending' ? (
                        <Clock className="w-4 h-4 text-red-500 mt-0.5 flex-shrink-0" />
                      ) : (
                        <CheckCircle className="w-4 h-4 text-green-500 mt-0.5 flex-shrink-0" />
                      )}
                      <div className="min-w-0">
                        <p className="text-sm font-medium">
                          Tier {e.tier} — {e.employee?.firstName} {e.employee?.lastName}
                        </p>
                        <p className="text-xs text-gray-500 mt-0.5">{e.details}</p>
                        <p className="text-xs text-gray-600 mt-0.5">
                          {new Date(e.triggeredAt).toLocaleString()}
                          {e.resolvedAt && ` · Resolved ${new Date(e.resolvedAt).toLocaleString()}`}
                        </p>
                      </div>
                    </div>
                    <div className="flex items-center gap-2 flex-shrink-0">
                      <span className={`text-xs px-2 py-0.5 rounded-full ${
                        e.status === 'pending'
                          ? 'bg-red-600/20 text-red-500'
                          : 'bg-green-600/20 text-green-500'
                      }`}>
                        {e.status}
                      </span>
                      {e.status === 'pending' && (
                        <button
                          onClick={() => handleAcknowledge(e.id)}
                          className="flex items-center gap-1 px-2.5 py-1.5 bg-green-600/10 hover:bg-green-600/20 text-green-500 rounded-lg text-xs transition-colors"
                        >
                          <CheckCircle className="w-3 h-3" /> Acknowledge
                        </button>
                      )}
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Create/Edit Modal */}
      {(showCreateModal || showEditModal) && (
        <PolicyFormModal
          title={showEditModal ? 'Edit Policy' : 'New Escalation Policy'}
          initial={editingPolicy || undefined}
          departments={departments}
          onSave={showEditModal ? handleUpdate : handleCreate}
          onClose={() => { setShowCreateModal(false); setShowEditModal(false); setEditingPolicy(null) }}
        />
      )}
    </div>
  )
}

function PolicyFormModal({
  title,
  initial,
  departments,
  onSave,
  onClose,
}: {
  title: string
  initial?: EscalationPolicy
  departments: Department[]
  onSave: (data: Partial<EscalationPolicy>) => Promise<void>
  onClose: () => void
}) {
  const [name, setName] = useState(initial?.name || '')
  const [departmentId, setDepartmentId] = useState<number | ''>(initial?.departmentId ?? '')
  const [maxResponseMinutes, setMaxResponseMinutes] = useState(initial?.maxResponseMinutes ?? 15)
  const [escalationTierCount, setEscalationTierCount] = useState(initial?.escalationTierCount ?? 3)
  const [notificationChannels, setNotificationChannels] = useState(initial?.notificationChannels || 'teams,email')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Policy name is required.'); return }
    setSaving(true)
    setError(null)
    try {
      await onSave({
        name: name.trim(),
        departmentId: departmentId || undefined,
        maxResponseMinutes,
        escalationTierCount,
        notificationChannels,
        isActive: true,
      })
    } catch {
      setError('Failed to save policy.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4 max-h-[90vh] overflow-y-auto" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{title}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />{error}
            </div>
          )}

          <div>
            <label className="block text-sm text-gray-500 mb-1">Policy Name</label>
            <input type="text" required value={name} onChange={e => setName(e.target.value)}
              placeholder="e.g., ER Night Escalation"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Department (optional)</label>
              <select value={departmentId} onChange={e => setDepartmentId(e.target.value ? Number(e.target.value) : '')}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600">
                <option value="">All Departments</option>
                {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
              </select>
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Max Response (minutes)</label>
              <input type="number" value={maxResponseMinutes} onChange={e => setMaxResponseMinutes(Number(e.target.value))}
                min={1} max={120}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Escalation Tiers</label>
              <input type="number" value={escalationTierCount} onChange={e => setEscalationTierCount(Number(e.target.value))}
                min={1} max={10}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Notification Channels</label>
              <input type="text" value={notificationChannels} onChange={e => setNotificationChannels(e.target.value)}
                placeholder="teams,email"
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600" />
            </div>
          </div>

          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={saving}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50">
              {saving ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Save className="w-4 h-4" />}
              {saving ? 'Saving...' : 'Save Policy'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
