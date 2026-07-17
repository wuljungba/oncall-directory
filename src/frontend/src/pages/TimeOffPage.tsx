import { useState } from 'react'
import { Calendar, Plus, Check, X } from 'lucide-react'

export default function TimeOffPage() {
  const [showForm, setShowForm] = useState(false)
  const [formData, setFormData] = useState({
    startDate: '',
    endDate: '',
    type: 'pto',
    notes: '',
  })

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    // TODO: Connect to API when backend is running
    console.log('Time off request:', formData)
    setShowForm(false)
    setFormData({ startDate: '', endDate: '', type: 'pto', notes: '' })
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
                onChange={(e) =>
                  setFormData({ ...formData, startDate: e.target.value })
                }
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">End Date</label>
              <input
                type="date"
                required
                value={formData.endDate}
                onChange={(e) =>
                  setFormData({ ...formData, endDate: e.target.value })
                }
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
          </div>
          <div>
            <label className="block text-sm text-gray-500 mb-1">Type</label>
            <select
              value={formData.type}
              onChange={(e) => setFormData({ ...formData, type: e.target.value })}
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
              onChange={(e) => setFormData({ ...formData, notes: e.target.value })}
              rows={3}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none"
            />
          </div>
          <div className="flex gap-2 pt-2">
            <button
              type="submit"
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Check className="w-4 h-4" />
              Submit Request
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
        <div className="px-5 py-4 border-b border-gray-800">
          <h2 className="font-medium">Upcoming & Pending</h2>
        </div>
        <div className="p-5">
          <div className="flex flex-col items-center justify-center py-12 text-gray-500">
            <Calendar className="w-12 h-12 mb-4 text-gray-700" />
            <p className="text-sm">No time off requests</p>
            <p className="text-xs mt-1">
              Click "Request Time Off" to submit your first request
            </p>
          </div>
        </div>
      </div>
    </div>
  )
}
