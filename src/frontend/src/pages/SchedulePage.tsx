import { useState, useEffect } from 'react'
import {
  ChevronLeft, ChevronRight, Clock, Plus, Save, X, Trash2, AlertTriangle, Sparkles
} from 'lucide-react'
import { scheduleApi, departmentsApi } from '@/services/api'
import type { Schedule, Shift, Department } from '@/types'

export default function SchedulePage() {
  const [schedules, setSchedules] = useState<Schedule[]>([])
  const [departments, setDepartments] = useState<Department[]>([])
  const [selectedSchedule, setSelectedSchedule] = useState<number | null>(null)
  const [shifts, setShifts] = useState<Shift[]>([])
  const [loading, setLoading] = useState(true)
  const [showCreateModal, setShowCreateModal] = useState(false)
  const [generating, setGenerating] = useState(false)
  const [currentWeekStart, setCurrentWeekStart] = useState(() => {
    const d = new Date()
    d.setDate(d.getDate() - d.getDay())
    d.setHours(0, 0, 0, 0)
    return d
  })

  useEffect(() => {
    Promise.all([
      scheduleApi.getAll(),
      departmentsApi.getAll(),
    ]).then(([scheds, depts]) => {
      setSchedules(scheds)
      setDepartments(depts)
      setLoading(false)
    }).catch(console.error)
  }, [])

  useEffect(() => {
    if (selectedSchedule) {
      const from = currentWeekStart.toISOString()
      const to = new Date(currentWeekStart.getTime() + 7 * 24 * 60 * 60 * 1000).toISOString()
      scheduleApi
        .getShifts(selectedSchedule, from, to)
        .then(setShifts)
        .catch(console.error)
    }
  }, [selectedSchedule, currentWeekStart])

  const weekDays = Array.from({ length: 7 }, (_, i) => {
    const d = new Date(currentWeekStart)
    d.setDate(d.getDate() + i)
    return d
  })

  const getShiftForCell = (day: Date, hour: number) => {
    return shifts.find((s) => {
      const start = new Date(s.startTime)
      const end = new Date(s.endTime)
      const cellStart = new Date(day)
      cellStart.setHours(hour, 0, 0, 0)
      const cellEnd = new Date(cellStart)
      cellEnd.setHours(hour + 1, 0, 0, 0)
      return start < cellEnd && end > cellStart
    })
  }

  const hasGap = (day: Date): boolean => {
    // Check if this day has no assigned shifts for any tier
    const dayShifts = shifts.filter((s) => {
      const start = new Date(s.startTime)
      return start.toDateString() === day.toDateString()
    })
    // If there are shifts and any are "gap" status or missing employee, highlight
    return dayShifts.length > 0 && dayShifts.some(s => s.status === 'gap' || !s.employeeId)
  }

  const tierColor = (tier: string) => {
    switch (tier) {
      case 'primary': return 'bg-amber-600/20 border-amber-600 text-amber-500'
      case 'secondary': return 'bg-blue-600/20 border-blue-600 text-blue-500'
      case 'tertiary': return 'bg-gray-600/20 border-gray-600 text-gray-400'
      default: return 'bg-gray-800 text-gray-500'
    }
  }

  async function handleGenerateShifts() {
    if (!selectedSchedule) return
    setGenerating(true)
    try {
      await scheduleApi.generateShifts(selectedSchedule, 4)
      // Refresh shifts for current view
      const from = currentWeekStart.toISOString()
      const to = new Date(currentWeekStart.getTime() + 7 * 24 * 60 * 60 * 1000).toISOString()
      const updated = await scheduleApi.getShifts(selectedSchedule, from, to)
      setShifts(updated)
    } catch (err) {
      console.error('Failed to generate shifts:', err)
    } finally {
      setGenerating(false)
    }
  }

  async function handleCreateSchedule(data: Partial<Schedule>) {
    try {
      const created = await scheduleApi.create(data)
      setSchedules(prev => [...prev, created])
      setSelectedSchedule(created.id)
      setShowCreateModal(false)
    } catch (err) {
      console.error('Failed to create schedule:', err)
    }
  }

  async function handleDeleteSchedule(id: number) {
    try {
      await scheduleApi.delete(id)
      setSchedules(prev => prev.filter(s => s.id !== id))
      if (selectedSchedule === id) {
        setSelectedSchedule(null)
        setShifts([])
      }
    } catch (err) {
      console.error('Failed to delete schedule:', err)
    }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">On-Call Schedule</h1>
        <div className="flex items-center gap-2">
          {selectedSchedule && (
            <>
              <button
                onClick={handleGenerateShifts}
                disabled={generating}
                className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm transition-colors disabled:opacity-50"
              >
                <Sparkles className="w-4 h-4" />
                {generating ? 'Generating...' : 'Auto-Generate Shifts'}
              </button>
              <button
                onClick={() => handleDeleteSchedule(selectedSchedule)}
                className="flex items-center gap-2 px-4 py-2 bg-red-600/10 hover:bg-red-600/20 text-red-400 rounded-lg text-sm transition-colors"
              >
                <Trash2 className="w-4 h-4" />
                Delete
              </button>
            </>
          )}
          <button
            onClick={() => setShowCreateModal(true)}
            className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
          >
            <Plus className="w-4 h-4" />
            New Schedule
          </button>
        </div>
      </div>

      {/* Schedule Selector */}
      <div className="flex items-center gap-4">
        <select
          className="bg-gray-900 border border-gray-700 rounded-lg px-4 py-2 text-sm flex-1"
          value={selectedSchedule ?? ''}
          onChange={(e) => setSelectedSchedule(Number(e.target.value) || null)}
        >
          <option value="">Select a schedule...</option>
          {schedules.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name} — {s.department?.name || 'No Department'}
            </option>
          ))}
        </select>
      </div>

      {/* Week Navigation */}
      {selectedSchedule && (
        <>
          <div className="flex items-center justify-between bg-gray-900 border border-gray-800 rounded-xl px-5 py-3">
            <button
              onClick={() => {
                const d = new Date(currentWeekStart)
                d.setDate(d.getDate() - 7)
                setCurrentWeekStart(d)
              }}
              className="p-2 hover:bg-gray-800 rounded-lg transition-colors"
            >
              <ChevronLeft className="w-5 h-5" />
            </button>
            <span className="text-sm font-medium">
              {weekDays[0].toLocaleDateString('en-US', { month: 'long', day: 'numeric' })} —{' '}
              {weekDays[6].toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}
            </span>
            <button
              onClick={() => {
                const d = new Date(currentWeekStart)
                d.setDate(d.getDate() + 7)
                setCurrentWeekStart(d)
              }}
              className="p-2 hover:bg-gray-800 rounded-lg transition-colors"
            >
              <ChevronRight className="w-5 h-5" />
            </button>
          </div>

          {/* Weekly Calendar Grid */}
          <div className="bg-gray-900 border border-gray-800 rounded-xl overflow-hidden">
            {/* Header */}
            <div className="grid grid-cols-[60px_repeat(7,1fr)] border-b border-gray-800">
              <div className="p-3 text-xs text-gray-500">Time</div>
              {weekDays.map((day, i) => (
                <div
                  key={i}
                  className={`p-3 text-center relative ${
                    day.toDateString() === new Date().toDateString()
                      ? 'bg-amber-600/5'
                      : ''
                  }`}
                >
                  <p className="text-xs text-gray-500">
                    {day.toLocaleDateString('en-US', { weekday: 'short' })}
                  </p>
                  <p className="text-sm font-medium mt-1">{day.getDate()}</p>
                  {/* Gap indicator */}
                  {hasGap(day) && (
                    <div className="absolute top-1 right-1 w-2 h-2 rounded-full bg-red-500" title="Coverage gap" />
                  )}
                </div>
              ))}
            </div>

            {/* Time rows */}
            {[0, 4, 8, 12, 16, 20].map((hour) => (
              <div
                key={hour}
                className="grid grid-cols-[60px_repeat(7,1fr)] border-b border-gray-800/50"
              >
                <div className="p-2 text-xs text-gray-600 border-r border-gray-800/50">
                  {hour.toString().padStart(2, '0')}:00
                </div>
                {weekDays.map((day, i) => {
                  const shift = getShiftForCell(day, hour)
                  const isGapCell = !shift || shift.status === 'gap'
                  return (
                    <div
                      key={i}
                      className={`p-1 min-h-[48px] border-r border-gray-800/50 last:border-r-0 ${
                        isGapCell ? 'bg-red-600/5' : ''
                      }`}
                    >
                      {shift ? (
                        <div
                          className={`text-[10px] p-1 rounded border ${tierColor(shift.tier)}`}
                          title={`${shift.employee?.firstName || 'Unassigned'} ${shift.employee?.lastName || ''} - ${shift.tier}`}
                        >
                          {shift.employee?.firstName?.charAt(0)}.{shift.employee?.lastName}
                          {shift.status === 'gap' && (
                            <span className="ml-1 text-red-400">(gap)</span>
                          )}
                        </div>
                      ) : null}
                    </div>
                  )
                })}
              </div>
            ))}
          </div>

          {/* Legend */}
          <div className="flex items-center gap-6 text-sm text-gray-500">
            <span className="flex items-center gap-2">
              <span className="w-3 h-3 rounded bg-amber-600/20 border border-amber-600" /> Primary
            </span>
            <span className="flex items-center gap-2">
              <span className="w-3 h-3 rounded bg-blue-600/20 border border-blue-600" /> Secondary
            </span>
            <span className="flex items-center gap-2">
              <span className="w-3 h-3 rounded bg-gray-600/20 border border-gray-600" /> Tertiary
            </span>
            <span className="flex items-center gap-2">
              <span className="w-3 h-3 rounded bg-red-600/20 border border-red-600" /> Gap / Unassigned
            </span>
          </div>
        </>
      )}

      {!selectedSchedule && (
        <div className="flex flex-col items-center justify-center py-20 text-gray-500">
          <Clock className="w-12 h-12 mb-4 text-gray-700" />
          <p>Select a schedule to view the weekly calendar</p>
          <p className="text-sm mt-2">
            No schedules yet? Click "New Schedule" to create one.
          </p>
        </div>
      )}

      {/* Create/Edit Schedule Modal */}
      {showCreateModal && (
        <CreateScheduleModal
          departments={departments}
          onSave={handleCreateSchedule}
          onClose={() => setShowCreateModal(false)}
        />
      )}
    </div>
  )
}

function CreateScheduleModal({
  departments,
  onSave,
  onClose,
}: {
  departments: Department[]
  onSave: (data: Partial<Schedule>) => Promise<void>
  onClose: () => void
}) {
  const [name, setName] = useState('')
  const [departmentId, setDepartmentId] = useState(departments[0]?.id || 0)
  const [rotationType, setRotationType] = useState<'weekly' | 'biweekly' | 'monthly'>('weekly')
  const [startDate, setStartDate] = useState('')
  const [endDate, setEndDate] = useState('')
  const [notes, setNotes] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) { setError('Schedule name is required.'); return }
    if (!startDate || !endDate) { setError('Start and end dates are required.'); return }
    if (endDate < startDate) { setError('End date must be after start date.'); return }

    setSaving(true)
    setError(null)
    await onSave({
      name: name.trim(),
      departmentId,
      rotationType,
      startDate: new Date(startDate).toISOString(),
      endDate: new Date(endDate).toISOString(),
      notes: notes.trim() || undefined,
    })
    setSaving(false)
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">New Schedule</h2>
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

          <div>
            <label className="block text-sm text-gray-500 mb-1">Schedule Name</label>
            <input
              type="text"
              required
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g., ER Attending Rotation - July"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Department</label>
              <select
                value={departmentId}
                onChange={(e) => setDepartmentId(Number(e.target.value))}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              >
                {departments.map((d) => (
                  <option key={d.id} value={d.id}>{d.name}</option>
                ))}
              </select>
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">Rotation Type</label>
              <select
                value={rotationType}
                onChange={(e) => setRotationType(e.target.value as typeof rotationType)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              >
                <option value="weekly">Weekly</option>
                <option value="biweekly">Bi-Weekly</option>
                <option value="monthly">Monthly</option>
              </select>
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Start Date</label>
              <input
                type="date"
                required
                value={startDate}
                onChange={(e) => setStartDate(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">End Date</label>
              <input
                type="date"
                required
                value={endDate}
                onChange={(e) => setEndDate(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Notes (optional)</label>
            <textarea
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              rows={2}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 resize-none"
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
              {saving ? 'Creating...' : 'Create Schedule'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
