import { useState, useEffect } from 'react'
import { Calendar, Plus, Check, X, AlertTriangle } from 'lucide-react'
import { scheduleApi } from '@/services/api'
import type { TimeOff } from '@/types'

type TimeOffType = TimeOff['type']
type TimeOffStatus = TimeOff['status']

const STATUS_COLORS: Record<TimeOffStatus, string> = {
  pending: 'bg-yellow-600/20 text-yellow-500',
  approved: 'bg-green-600/20 text-green-500',
  denied: 'bg-red-600/20 text-red-500',
}

const TYPE_LABELS: Record<TimeOffType, string> = {
  pto: 'Paid Time Off',
  cme: 'CME / Conference',
  holiday: 'Holiday',
  sick: 'Sick Leave',
}

export default function TimeOffPage() {
  const [requests, setRequests] = useState<TimeOff[]>([])
  const [loading, setLoading] = useState(true)
  const [showForm, setShowForm] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [formErrors, setFormErrors] = useState<Record<string, string>>({})
  const [formData, setFormData] = useState({
    startDate: '',
    endDate: '',
    type: 'pto' as TimeOffType,
    notes: '',
  })

  useEffect(() => {
    loadRequests()
  }, [])

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
      await scheduleApi.requestTimeOff({
        startDate: formData.startDate,
        endDate: formData.endDate,
        type: formData.type,
        notes: formData.notes || undefined,
      })
      setShowForm(false)
      setFormData({ startDate: '', endDate: '', type: 'pto', notes: '' })
      await loadRequests()
    } catch (err) {
      console.error('Failed to submit time-off request:', err)
      setError('Failed to submit request. Please try again.')
    } finally {
      setSubmitting(false)
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
          <h2 className="font-medium">New Time Off Request</h2>
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
              onClick={() => setShowForm(false)}
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
                  className="flex items-center justify-between p-4 bg-gray-800/50 rounded-lg"
                >
                  <div>
                    <p className="text-sm font-medium">
                      {TYPE_LABELS[req.type] || req.type}
                    </p>
                    <p className="text-xs text-gray-500 mt-0.5">
                      {new Date(req.startDate).toLocaleDateString()} —{' '}
                      {new Date(req.endDate).toLocaleDateString()}
                    </p>
                    {req.notes && (
                      <p className="text-xs text-gray-600 mt-1">{req.notes}</p>
                    )}
                  </div>
                  <span
                    className={`text-xs px-2 py-0.5 rounded-full ${
                      STATUS_COLORS[req.status] || 'bg-gray-600/20 text-gray-400'
                    }`}
                  >
                    {req.status}
                  </span>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
