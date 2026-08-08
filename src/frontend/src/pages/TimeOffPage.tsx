import { useState, useEffect, useRef } from 'react'
import { Calendar, Plus, Check, X, AlertTriangle, Edit3, Trash2, Users } from 'lucide-react'
import { scheduleApi } from '@/services/api'
import { useSignalR } from '@/hooks/useSignalR'
import { formatDateOnly } from '@/utils/date'
import type { TimeOff } from '@/types'

type TimeOffType = TimeOff['type']
type TimeOffStatus = TimeOff['status']

const STATUS_COLORS: Record<TimeOffStatus, string> = {
  pending: 'bg-yellow-600/20 text-yellow-500',
  approved: 'bg-green-600/20 text-green-500',
  denied: 'bg-red-600/20 text-red-500',
}

const TYPE_LABELS: Record<string, string> = {
  pto: 'Paid Time Off',
  cme: 'CME / Conference',
  holiday: 'Holiday',
  sick: 'Sick Leave',
  personal: 'Personal Leave',
  bereavement: 'Bereavement',
  military: 'Military Leave',
  jury_duty: 'Jury Duty',
  unpaid: 'Unpaid Leave',
}

export default function TimeOffPage() {
  const [requests, setRequests] = useState<TimeOff[]>([])
  const [loading, setLoading] = useState(true)
  const [teamRequests, setTeamRequests] = useState<TimeOff[]>([])
  const [teamLoading, setTeamLoading] = useState(true)
  const [showForm, setShowForm] = useState(false)
  const [editingRequest, setEditingRequest] = useState<TimeOff | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [formErrors, setFormErrors] = useState<Record<string, string>>({})
  const [formData, setFormData] = useState({
    startDate: '',
    endDate: '',
    type: 'pto' as TimeOffType,
    notes: '',
  })
  const { lastEvent } = useSignalR()
  const prevEventRef = useRef<string | null>(null)

  useEffect(() => {
    loadRequests()
    loadTeamRequests()
  }, [])

  async function loadTeamRequests() {
    try {
      setTeamLoading(true)
      const data = await scheduleApi.getTimeOffReview()
      setTeamRequests(data)
    } catch {
      // 403 when the caller has no manager profile — silently show no team requests.
      setTeamRequests([])
    } finally {
      setTeamLoading(false)
    }
  }

  // Re-fetch on SignalR events
  useEffect(() => {
    if (!lastEvent || lastEvent.type !== 'TimeOffUpdated') return
    const eventKey = `${lastEvent.type}-${JSON.stringify(lastEvent.payload)}`
    if (eventKey === prevEventRef.current) return
    prevEventRef.current = eventKey
    loadRequests()
    loadTeamRequests()
  }, [lastEvent])

  async function loadRequests() {
    try {
      setLoading(true)
      const data = await scheduleApi.getMyTimeOff()
      setRequests(data)
    } catch (err) {
      console.error('Failed to load time-off requests:', err)
      setError('Could not load time-off requests. Is the backend running?')
    } finally {
      setLoading(false)
    }
  }

  function resetForm() {
    setFormData({ startDate: '', endDate: '', type: 'pto', notes: '' })
    setEditingRequest(null)
    setShowForm(false)
    setFormErrors({})
  }

  function openEdit(request: TimeOff) {
    setEditingRequest(request)
    setFormData({
      startDate: request.startDate.split('T')[0],
      endDate: request.endDate.split('T')[0],
      type: request.type,
      notes: request.notes || '',
    })
    setShowForm(true)
  }

  function validate(): boolean {
    const errors: Record<string, string> = {}
    if (!formData.startDate) {
      errors.startDate = 'Start date is required'
    }
    if (!formData.endDate) {
      errors.endDate = 'End date is required'
    }
    if (formData.startDate && formData.endDate && formData.endDate < formData.startDate) {
      errors.endDate = 'End date must be on or after start date'
    }
    setFormErrors(errors)
    return Object.keys(errors).length === 0
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!validate()) return
    setSubmitting(true)
    setError(null)
    try {
      if (editingRequest) {
        await scheduleApi.updateTimeOff(editingRequest.id, {
          startDate: formData.startDate,
          endDate: formData.endDate,
          type: formData.type,
          notes: formData.notes || undefined,
        })
      } else {
        await scheduleApi.requestTimeOff({
          startDate: formData.startDate,
          endDate: formData.endDate,
          type: formData.type,
          notes: formData.notes || undefined,
        })
      }
      resetForm()
      await loadRequests()
    } catch (err) {
      console.error('Failed to save time-off request:', err)
      setError('Failed to save request. Please try again.')
    } finally {
      setSubmitting(false)
    }
  }

  async function handleCancel(id: number) {
    if (!confirm('Are you sure you want to cancel this time-off request?')) return
    try {
      setError(null)
      await scheduleApi.cancelTimeOff(id)
      await loadRequests()
    } catch (err) {
      console.error('Failed to cancel time-off request:', err)
      setError('Failed to cancel request.')
    }
  }

  async function handleTeamDecision(request: TimeOff, approve: boolean) {
    const who = request.employee ? `${request.employee.firstName} ${request.employee.lastName}` : 'this employee'
    const reason = window.prompt(`${who}'s request — ${approve ? 'approve' : 'deny'} (optional reason):`, '') ?? ''
    setError(null)
    try {
      if (approve) {
        await scheduleApi.approveTimeOff(request.id, reason || undefined)
      } else {
        await scheduleApi.denyTimeOff(request.id, reason || undefined)
      }
      await Promise.all([loadRequests(), loadTeamRequests()])
    } catch {
      setError(approve ? 'Failed to approve request.' : 'Failed to deny request.')
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Time Off</h1>
        <button
          onClick={() => setShowForm(!showForm)}
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
        >
          <Plus className="w-4 h-4" />
          Request Time Off
        </button>
      </div>

      {/* Error banner */}
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {/* Request Form */}
      {showForm && (
        <form
          onSubmit={handleSubmit}
          className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4"
        >
          <h2 className="font-medium">{editingRequest ? 'Edit Time Off Request' : 'New Time Off Request'}</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Start Date</label>
              <input
                type="date"
                required
                value={formData.startDate}
                onChange={(e) => {
                  setFormData({ ...formData, startDate: e.target.value })
                  setFormErrors((prev) => ({ ...prev, startDate: '' }))
                }}
                className={`w-full bg-gray-800 border rounded-lg px-4 py-2 text-sm focus:outline-none ${
                  formErrors.startDate ? 'border-red-500' : 'border-gray-700 focus:border-amber-600'
                }`}
              />
              {formErrors.startDate && (
                <p className="text-red-500 text-xs mt-1">{formErrors.startDate}</p>
              )}
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">End Date</label>
              <input
                type="date"
                required
                value={formData.endDate}
                onChange={(e) => {
                  setFormData({ ...formData, endDate: e.target.value })
                  setFormErrors((prev) => ({ ...prev, endDate: '' }))
                }}
                className={`w-full bg-gray-800 border rounded-lg px-4 py-2 text-sm focus:outline-none ${
                  formErrors.endDate ? 'border-red-500' : 'border-gray-700 focus:border-amber-600'
                }`}
              />
              {formErrors.endDate && (
                <p className="text-red-500 text-xs mt-1">{formErrors.endDate}</p>
              )}
            </div>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Type</label>
            <select
              value={formData.type}
              onChange={(e) =>
                setFormData({ ...formData, type: e.target.value as TimeOffType })
              }
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            >
              <option value="pto">Paid Time Off</option>
              <option value="cme">CME / Conference</option>
              <option value="holiday">Holiday</option>
              <option value="sick">Sick Leave</option>
              <option value="personal">Personal Leave</option>
              <option value="bereavement">Bereavement</option>
              <option value="military">Military Leave</option>
              <option value="jury_duty">Jury Duty</option>
              <option value="unpaid">Unpaid Leave</option>
            </select>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Notes</label>
            <textarea
              value={formData.notes}
              onChange={(e) =>
                setFormData({ ...formData, notes: e.target.value })
              }
              rows={3}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none"
            />
          </div>
          <div className="flex gap-2 pt-2">
            <button
              type="submit"
              disabled={submitting}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 disabled:opacity-50 rounded-lg text-sm font-medium transition-colors"
            >
              {submitting ? (
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
              ) : (
                <Check className="w-4 h-4" />
              )}
              {submitting ? 'Submitting...' : 'Submit Request'}
            </button>
            <button
              type="button"
              onClick={resetForm}
              className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm transition-colors"
            >
              <X className="w-4 h-4" />
              Cancel
            </button>
          </div>
        </form>
      )}

      {/* Existing Requests */}
      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between">
          <h2 className="font-medium">My Requests</h2>
          {!loading && (
            <span className="text-xs text-gray-500">
              {requests.length} request{requests.length !== 1 ? 's' : ''}
            </span>
          )}
        </div>
        <div className="p-5">
          {loading ? (
            <div className="flex items-center justify-center py-12">
              <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" />
            </div>
          ) : requests.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 text-gray-500">
              <Calendar className="w-12 h-12 mb-4 text-gray-700" />
              <p className="text-sm">No time off requests</p>
              <p className="text-xs mt-1">
                Click "Request Time Off" to submit your first request
              </p>
            </div>
          ) : (
            <div className="space-y-3">
              {requests.map((req) => (
                <div
                  key={req.id}
                  className="flex items-center justify-between p-4 bg-gray-800/50 rounded-lg group"
                >
                  <div>
                    <p className="text-sm font-medium">
                      {TYPE_LABELS[req.type] || req.type}
                    </p>
                    <p className="text-xs text-gray-500 mt-0.5">
                      {formatDateOnly(req.startDate)} —{' '}
                      {formatDateOnly(req.endDate)}
                    </p>
                    {req.notes && (
                      <p className="text-xs text-gray-600 mt-1">{req.notes}</p>
                    )}
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
                      <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                        <button
                          onClick={() => openEdit(req)}
                          className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors"
                          title="Edit"
                        >
                          <Edit3 className="w-3.5 h-3.5 text-gray-400 hover:text-amber-400" />
                        </button>
                        <button
                          onClick={() => handleCancel(req.id)}
                          className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors"
                          title="Cancel request"
                        >
                          <Trash2 className="w-3.5 h-3.5 text-gray-400 hover:text-red-400" />
                        </button>
                      </div>
                    )}
                    <span
                      className={`text-xs px-2 py-0.5 rounded-full ${
                        STATUS_COLORS[req.status] || 'bg-gray-600/20 text-gray-400'
                      }`}
                    >
                      {req.status}
                    </span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Team Requests — pending requests from the current user's direct reports */}
      {!teamLoading && teamRequests.length > 0 && (
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between">
            <h2 className="font-medium flex items-center gap-2">
              <Users className="w-4 h-4 text-amber-500" /> Team Requests (Pending)
            </h2>
            <span className="text-xs text-gray-500">{teamRequests.length} awaiting approval</span>
          </div>
          <div className="divide-y divide-gray-800">
            {teamRequests.map(req => (
              <div key={req.id} className="px-5 py-4 flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="text-sm font-medium">
                    {req.employee?.firstName} {req.employee?.lastName}
                  </p>
                  <p className="text-xs text-gray-500 mt-0.5">
                    {TYPE_LABELS[req.type] || req.type} · {formatDateOnly(req.startDate)} — {formatDateOnly(req.endDate)}
                  </p>
                  {req.notes && <p className="text-xs text-gray-600 mt-1 truncate">{req.notes}</p>}
                </div>
                <div className="flex items-center gap-2 flex-shrink-0">
                  <button
                    onClick={() => handleTeamDecision(req, true)}
                    className="flex items-center gap-1 px-3 py-1.5 bg-green-600/20 hover:bg-green-600/30 text-green-500 rounded-lg text-xs transition-colors"
                  >
                    <Check className="w-3.5 h-3.5" /> Approve
                  </button>
                  <button
                    onClick={() => handleTeamDecision(req, false)}
                    className="flex items-center gap-1 px-3 py-1.5 bg-red-600/20 hover:bg-red-600/30 text-red-500 rounded-lg text-xs transition-colors"
                  >
                    <X className="w-3.5 h-3.5" /> Deny
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
