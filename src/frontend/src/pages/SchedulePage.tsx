import { useState, useEffect } from 'react'
import { ChevronLeft, ChevronRight, Clock, UserPlus } from 'lucide-react'
import { scheduleApi } from '@/services/api'
import type { Schedule, Shift } from '@/types'

export default function SchedulePage() {
  const [schedules, setSchedules] = useState<Schedule[]>([])
  const [selectedSchedule, setSelectedSchedule] = useState<number | null>(null)
  const [shifts, setShifts] = useState<Shift[]>([])
  const [currentWeekStart, setCurrentWeekStart] = useState(() => {
    const d = new Date()
    d.setDate(d.getDate() - d.getDay())
    d.setHours(0, 0, 0, 0)
    return d
  })

  useEffect(() => {
    scheduleApi.getAll().then(setSchedules).catch(console.error)
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

  const hours = Array.from({ length: 24 }, (_, i) => i)

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

  const tierColor = (tier: string) => {
    switch (tier) {
      case 'primary': return 'bg-amber-600/20 border-amber-600 text-amber-500'
      case 'secondary': return 'bg-blue-600/20 border-blue-600 text-blue-500'
      case 'tertiary': return 'bg-gray-600/20 border-gray-600 text-gray-400'
      default: return 'bg-gray-800 text-gray-500'
    }
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">On-Call Schedule</h1>
        <select
          className="bg-gray-900 border border-gray-700 rounded-lg px-4 py-2 text-sm"
          value={selectedSchedule ?? ''}
          onChange={(e) => setSelectedSchedule(Number(e.target.value) || null)}
        >
          <option value="">Select a schedule...</option>
          {schedules.map((s) => (
            <option key={s.id} value={s.id}>
              {s.name} — {s.department?.name}
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
                  className={`p-3 text-center ${
                    day.toDateString() === new Date().toDateString()
                      ? 'bg-amber-600/5'
                      : ''
                  }`}
                >
                  <p className="text-xs text-gray-500">
                    {day.toLocaleDateString('en-US', { weekday: 'short' })}
                  </p>
                  <p className="text-sm font-medium mt-1">{day.getDate()}</p>
                </div>
              ))}
            </div>

            {/* Time rows (every 4 hours) */}
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
                  return (
                    <div
                      key={i}
                      className="p-1 min-h-[48px] border-r border-gray-800/50 last:border-r-0"
                    >
                      {shift && (
                        <div
                          className={`text-[10px] p-1 rounded border ${tierColor(shift.tier)}`}
                          title={shift.employee?.firstName + ' ' + shift.employee?.lastName}
                        >
                          {shift.employee?.firstName?.charAt(0)}.{shift.employee?.lastName}
                        </div>
                      )}
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
          </div>
        </>
      )}

      {!selectedSchedule && (
        <div className="flex flex-col items-center justify-center py-20 text-gray-500">
          <Clock className="w-12 h-12 mb-4 text-gray-700" />
          <p>Select a schedule to view the weekly calendar</p>
          <p className="text-sm mt-2">
            No schedules yet? Create one to get started.
          </p>
        </div>
      )}
    </div>
  )
}
