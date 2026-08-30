import { useState, useEffect, useRef } from 'react'
import {
  ChevronLeft, ChevronRight, Clock, Plus, Save, X, Trash2, AlertTriangle,
  Sparkles, Repeat, Download, Phone, Mail, MessageSquare,
} from 'lucide-react'
import { scheduleApi, departmentsApi, directoryApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import { useSignalR } from '@/hooks/useSignalR'
import { formatTimeRange, formatDateOnly } from '@/utils/date'
import type { Schedule, Shift, Department, Employee } from '@/types'
import { downloadBlob } from '@/utils/download'

export default function SchedulePage() {
  const [schedules, setSchedules] = useState<Schedule[]>([])
  const [departments, setDepartments] = useState<Department[]>([])
  const [selectedSchedule, setSelectedSchedule] = useState<number | null>(null)
  const [shifts, setShifts] = useState<Shift[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [showCreateModal, setShowCreateModal] = useState(false)
  const [generating, setGenerating] = useState(false)
  const [swapTargetShift, setSwapTargetShift] = useState<Shift | null>(null)
  const [showSwapModal, setShowSwapModal] = useState(false)
  const [showAssignModal, setShowAssignModal] = useState(false)
  const [assignCellStart, setAssignCellStart] = useState<Date | null>(null)
  const [assignCellEnd, setAssignCellEnd] = useState<Date | null>(null)
  const [currentOnCall, setCurrentOnCall] = useState<Shift[]>([])
  const [viewMode, setViewMode] = useState<'weekly' | 'biweekly' | 'monthly'>('weekly')
  const DAYS_COUNT = viewMode === 'weekly' ? 7 : viewMode === 'biweekly' ? 14 : 28
  const gridCols = `60px repeat(${DAYS_COUNT}, minmax(100px, 1fr))`
  const { canScheduleWrite, canAdminFull } = useAuth()
  const [currentWeekStart, setCurrentWeekStart] = useState(() => {
    const d = new Date()
    d.setDate(d.getDate() - d.getDay())
    d.setHours(0, 0, 0, 0)
    return d
  })

  function getWeekStart(date: Date) {
    const d = new Date(date)
    d.setDate(d.getDate() - d.getDay())
    d.setHours(0, 0, 0, 0)
    return d
  }

  useEffect(() => {
    Promise.all([
      scheduleApi.getAll(),
      departmentsApi.getAll(),
    ]).then(([scheds, depts]) => {
      setSchedules(scheds)
      setDepartments(depts)
      setError(null)
      setLoading(false)
    }).catch(err => {
      console.error(err)
      setError('Failed to load schedules and departments.')
      setLoading(false)
    })
  }, [])

  useEffect(() => {
    if (selectedSchedule) {
      const from = currentWeekStart.toISOString()
      const to = new Date(currentWeekStart.getTime() + DAYS_COUNT * 24 * 60 * 60 * 1000).toISOString()
      scheduleApi
        .getShifts(selectedSchedule, from, to)
        .then(data => {
          setShifts(data)
          setError(null)
        })
        .catch(err => {
          console.error(err)
          setError('Failed to load shifts for this schedule.')
        })
    }
  }, [selectedSchedule, currentWeekStart, DAYS_COUNT])

  useEffect(() => {
    const currentSchedule = schedules.find(s => s.id === selectedSchedule)
    scheduleApi.getOnCall(currentSchedule?.departmentId)
      .then(data => {
        setCurrentOnCall(data)
        setError(null)
      })
      .catch(err => {
        console.error(err)
        setError('Failed to load on-call data.')
      })
  }, [selectedSchedule, schedules])

  // ── SignalR real-time subscriptions ──
  const { lastEvent } = useSignalR()
  const prevLastEventRef = useRef<string | null>(null)

  useEffect(() => {
    if (!lastEvent || !selectedSchedule) return

    // Deduplicate: skip if this event key was already processed
    const eventKey = `${lastEvent.type}-${JSON.stringify(lastEvent.payload)}`
    if (eventKey === prevLastEventRef.current) return
    prevLastEventRef.current = eventKey

    const refreshOn = ['ShiftAssigned', 'ShiftsGenerated', 'SwapRequested', 'SwapApproved']
    if (!refreshOn.includes(lastEvent.type)) return

    // Refresh shifts for current view
    const from = currentWeekStart.toISOString()
    const to = new Date(currentWeekStart.getTime() + DAYS_COUNT * 24 * 60 * 60 * 1000).toISOString()
    scheduleApi.getShifts(selectedSchedule, from, to)
      .then(data => {
        setShifts(data)
        setError(null)
      })
      .catch(err => {
        console.error('Failed to refresh shifts:', err)
        setError('Failed to refresh shifts after update.')
      })

    // Also refresh on-call data
    const currentSchedule = schedules.find(s => s.id === selectedSchedule)
    if (currentSchedule) {
      scheduleApi.getOnCall(currentSchedule.departmentId)
        .then(data => {
          setCurrentOnCall(data)
          setError(null)
        })
        .catch(err => {
          console.error('Failed to refresh on-call:', err)
          setError('Failed to refresh on-call data.')
        })
    }

    // If a new schedule was created, refresh the schedule list
    if (lastEvent.type === 'ScheduleCreated') {
      scheduleApi.getAll()
        .then(data => {
          setSchedules(data)
          setError(null)
          // Auto-select if only one schedule
          if (data.length === 1) setSelectedSchedule(data[0].id)
        })
        .catch(err => {
          console.error('Failed to refresh schedules:', err)
          setError('Failed to refresh schedule list.')
        })
    }
  }, [lastEvent, selectedSchedule, currentWeekStart, DAYS_COUNT, schedules])

  const weekDays = Array.from({ length: DAYS_COUNT }, (_, i) => {
    const d = new Date(currentWeekStart)
    d.setDate(d.getDate() + i)
    return d
  })

  // 4-hour time blocks covering a full day continuously
  const TIME_BLOCKS = [
    { label: '00:00', hour: 0 },
    { label: '04:00', hour: 4 },
    { label: '08:00', hour: 8 },
    { label: '12:00', hour: 12 },
    { label: '16:00', hour: 16 },
    { label: '20:00', hour: 20 },
  ]

  const getShiftForCell = (day: Date, blockHour: number) => {
    return shifts.find((s) => {
      const start = new Date(s.startTime)
      const end = new Date(s.endTime)
      const cellStart = new Date(day)
      cellStart.setHours(blockHour, 0, 0, 0)
      const cellEnd = new Date(cellStart)
      // Each block spans 4 hours; the 20:00 block wraps to next day 00:00
      cellEnd.setHours(blockHour === 20 ? 24 : blockHour + 4, 0, 0, 0)
      return start < cellEnd && end > cellStart
    })
  }

  const getGapDays = (): Date[] => {
    // Returns days that have coverage gaps (unassigned shifts)
    const gapDays: Date[] = []
    for (const day of weekDays) {
      const dayShifts = shifts.filter((s) => {
        const start = new Date(s.startTime)
        return start.toDateString() === day.toDateString()
      })
      if (dayShifts.length > 0 && dayShifts.some(s => s.status === 'gap' || !s.employeeId)) {
        gapDays.push(day)
      }
    }
    return gapDays
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
    setError(null)
    try {
      await scheduleApi.generateShifts(selectedSchedule, 4)
      // Refresh shifts for current view
      const from = currentWeekStart.toISOString()
      const to = new Date(currentWeekStart.getTime() + DAYS_COUNT * 24 * 60 * 60 * 1000).toISOString()
      const updated = await scheduleApi.getShifts(selectedSchedule, from, to)
      setShifts(updated)
    } catch (err) {
      console.error('Failed to generate shifts:', err)
      setError('Failed to generate shifts. Please try again.')
    } finally {
      setGenerating(false)
    }
  }

  async function handleRequestSwap(shiftId: number, replacementUserId: string, reason: string) {
    setError(null)
    try {
      await scheduleApi.requestSwap({
        shiftId,
        replacementUserId,
        reason,
      })
      setShowSwapModal(false)
      setSwapTargetShift(null)
    } catch (err) {
      console.error('Failed to request swap:', err)
      setError('Failed to request shift swap. Please try again.')
    }
  }

  function handleDownloadIcs() {
    if (!selectedSchedule || shifts.length === 0) return

    const lines: string[] = [
      'BEGIN:VCALENDAR',
      'VERSION:2.0',
      'PRODID:-//OnCall//Schedule//EN',
      'CALSCALE:GREGORIAN',
    ]

    for (const s of shifts) {
      const start = new Date(s.startTime)
      const end = new Date(s.endTime)
      const uid = `shift-${s.id}@oncall`

      const fmt = (d: Date) =>
        d.toISOString().replace(/[-:]/g, '').split('.')[0] + 'Z'

      lines.push('BEGIN:VEVENT')
      lines.push(`UID:${uid}`)
      lines.push(`DTSTART:${fmt(start)}`)
      lines.push(`DTEND:${fmt(end)}`)
      lines.push(`SUMMARY:On-Call (${s.tier}) - ${s.employee?.firstName || ''} ${s.employee?.lastName || ''}`.trim())
      if (s.notes) lines.push(`DESCRIPTION:${s.notes}`)
      lines.push('END:VEVENT')
    }

    lines.push('END:VCALENDAR')

    const blob = new Blob([lines.join('\r\n')], { type: 'text/calendar;charset=utf-8' })
    downloadBlob(blob, `oncall-schedule-${selectedSchedule}.ics`)
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

  async function handleAssignShift(employeeId: string, startTime: string, endTime: string, tier: string) {
    if (!selectedSchedule) return
    try {
      await scheduleApi.assignShift(selectedSchedule, {
        employeeId,
        startTime,
        endTime,
        tier: tier as 'primary' | 'secondary' | 'tertiary',
      })
      // Refresh shifts for current view
      const from = currentWeekStart.toISOString()
      const to = new Date(currentWeekStart.getTime() + DAYS_COUNT * 24 * 60 * 60 * 1000).toISOString()
      const updated = await scheduleApi.getShifts(selectedSchedule, from, to)
      setShifts(updated)
      setShowAssignModal(false)
      setAssignCellStart(null)
      setAssignCellEnd(null)
    } catch (err) {
      console.error('Failed to assign shift:', err)
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
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" />
          <span>{error}</span>
          <button
            onClick={() => setError(null)}
            className="ml-auto text-red-400 hover:text-red-300"
          >
            <X className="w-4 h-4" />
          </button>
        </div>
      )}
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">On-Call Schedule</h1>
        <div className="flex items-center gap-2">
          {selectedSchedule && (
            <>
              {canScheduleWrite && (
                <button
                  onClick={handleGenerateShifts}
                  disabled={generating}
                  className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm transition-colors disabled:opacity-50"
                >
                  <Sparkles className="w-4 h-4" />
                  {generating ? 'Generating...' : 'Auto-Generate Shifts'}
                </button>
              )}
              <button
                onClick={handleDownloadIcs}
                className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 rounded-lg text-sm transition-colors"
                title="Download as .ics (import into Outlook/Google Calendar)"
              >
                <Download className="w-4 h-4" />
                Export
              </button>
              {canAdminFull && (
                <button
                  onClick={() => handleDeleteSchedule(selectedSchedule)}
                  className="flex items-center gap-2 px-4 py-2 bg-red-600/10 hover:bg-red-600/20 text-red-400 rounded-lg text-sm transition-colors"
                >
                  <Trash2 className="w-4 h-4" />
                  Delete
                </button>
              )}
            </>
          )}
          {canScheduleWrite && (
            <button
              onClick={() => setShowCreateModal(true)}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
            >
              <Plus className="w-4 h-4" />
              New Schedule
            </button>
          )}
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
            <div className="flex items-center gap-3">
              <button
                onClick={() => {
                  const d = new Date()
                  d.setDate(d.getDate() - d.getDay())
                  d.setHours(0, 0, 0, 0)
                  setCurrentWeekStart(d)
                }}
                className={`px-3 py-1.5 rounded-lg text-xs transition-colors ${
                  currentWeekStart.toDateString() === getWeekStart(new Date()).toDateString()
                    ? 'bg-amber-600/20 text-amber-500 cursor-default'
                    : 'bg-gray-800 hover:bg-gray-700 text-gray-400'
                }`}
              >
                Today
              </button>
              {/* View Mode Toggle */}
              <div className="flex bg-gray-800 rounded-lg p-0.5">
                {(['weekly', 'biweekly', 'monthly'] as const).map((mode) => (
                  <button
                    key={mode}
                    onClick={() => {
                      setViewMode(mode)
                      // Reset to week start when changing view mode
                      const d = new Date()
                      d.setDate(d.getDate() - d.getDay())
                      d.setHours(0, 0, 0, 0)
                      setCurrentWeekStart(d)
                    }}
                    className={`px-2.5 py-1 rounded-md text-xs font-medium capitalize transition-colors ${
                      viewMode === mode
                        ? 'bg-amber-600 text-white'
                        : 'text-gray-400 hover:text-gray-200'
                    }`}
                  >
                    {mode === 'weekly' ? 'Week' : mode === 'biweekly' ? '2 Weeks' : 'Month'}
                  </button>
                ))}
              </div>
              <span className="text-sm font-medium">
                {weekDays[0].toLocaleDateString('en-US', { month: 'long', day: 'numeric' })} —{' '}
                {weekDays[weekDays.length - 1].toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}
              </span>
              {(() => {
                const gaps = getGapDays()
                if (gaps.length > 0) {
                  return (
                    <button
                      onClick={() => {
                        const gapDate = gaps[0].toLocaleDateString('en-US', { weekday: 'long' })
                        alert(`Coverage needed for: ${gapDate} (${gaps.length} day${gaps.length > 1 ? 's' : ''} with gaps)`)
                      }}
                      className="flex items-center gap-1.5 px-3 py-1.5 bg-red-600/10 hover:bg-red-600/20 text-red-400 rounded-lg text-xs transition-colors"
                    >
                      <AlertTriangle className="w-3.5 h-3.5" />
                      {gaps.length} gap{gaps.length > 1 ? 's' : ''} — Find Coverage
                    </button>
                  )
                }
                return null
              })()}
            </div>
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

          {/* Currently On Call Banner */}
          {currentOnCall.length > 0 && (
            <div className="bg-gray-900 border border-amber-600/30 rounded-xl px-5 py-3">
              <div className="flex items-center gap-2 mb-2">
                <Clock className="w-4 h-4 text-amber-500" />
                <span className="text-sm font-medium text-amber-500">Currently On Call</span>
              </div>
              <div className="flex flex-wrap gap-3">
                {currentOnCall.map((s, i) => (
                  <div key={i} className="flex items-center gap-3 bg-gray-800/50 rounded-lg px-4 py-2 text-sm">
                    <div className="flex items-center gap-2">
                      <div className="w-8 h-8 rounded-full bg-gray-700 flex items-center justify-center text-xs font-medium">
                        {s.employee?.firstName?.charAt(0)}{s.employee?.lastName?.charAt(0)}
                      </div>
                      <div>
                        <p className="font-medium">{s.employee?.firstName} {s.employee?.lastName}</p>
                        <p className="text-xs text-gray-500">
                          <span className={`inline-block px-1.5 py-0.5 rounded text-[10px] ${tierColor(s.tier)}`}>
                            {s.tier}
                          </span>
                          {' '}{formatTimeRange(s.startTime, s.endTime)}
                        </p>
                      </div>
                    </div>
                    <div className="flex items-center gap-1 ml-2">
                      {s.employee?.officePhone && (
                        <a
                          href={`tel:${s.employee.officePhone}`}
                          className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors"
                          title={`Call ${s.employee.officePhone}`}
                        >
                          <Phone className="w-3.5 h-3.5 text-gray-400 hover:text-green-400" />
                        </a>
                      )}
                      {s.employee?.email && (
                        <a
                          href={`mailto:${s.employee.email}`}
                          className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors"
                          title="Send email"
                        >
                          <Mail className="w-3.5 h-3.5 text-gray-400 hover:text-amber-400" />
                        </a>
                      )}
                      {s.employee?.email && (
                        <a
                          href={`https://teams.microsoft.com/l/chat/0/0?users=${encodeURIComponent(s.employee.email)}`}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors"
                          title="Chat in Teams"
                        >
                          <MessageSquare className="w-3.5 h-3.5 text-gray-400 hover:text-blue-400" />
                        </a>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {currentOnCall.length === 0 && selectedSchedule && (
            <div className="flex items-center gap-2 bg-gray-900 border border-gray-800 rounded-xl px-5 py-3 text-sm text-gray-500">
              <AlertTriangle className="w-4 h-4 text-gray-600" />
              No one is currently on call for this schedule
            </div>
          )}

          {/* Weekly Calendar Grid */}
          <div className="bg-gray-900 border border-gray-800 rounded-xl overflow-x-auto">
            {/* Header */}
            <div className="grid border-b border-gray-800" style={{ gridTemplateColumns: gridCols }}>
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
                  {(() => {
                    const dayShifts = shifts.filter((s) => {
                      const start = new Date(s.startTime)
                      return start.toDateString() === day.toDateString()
                    })
                    return dayShifts.length > 0 && dayShifts.some(s => s.status === 'gap' || !s.employeeId)
                  })() && (
                    <div className="absolute top-1 right-1 w-2 h-2 rounded-full bg-red-500" title="Coverage gap" />
                  )}
                </div>
              ))}
            </div>

            {/* Time rows — continuous 4-hour blocks */}
            {TIME_BLOCKS.map((block) => (
              <div
                key={block.hour}
                className="grid border-b border-gray-800/50" style={{ gridTemplateColumns: gridCols }}
              >
                <div className="p-2 text-xs text-gray-600 border-r border-gray-800/50">
                  {block.label}{block.hour === 20 ? <span className="block text-[9px] text-gray-700">→ 00:00</span> : ''}
                </div>
                {weekDays.map((day, i) => {
                  const shift = getShiftForCell(day, block.hour)
                  const isGapCell = !shift || shift.status === 'gap'
                  return (
                    <div
                      key={i}
                      className={`p-1 min-h-[48px] border-r border-gray-800/50 last:border-r-0 ${
                        isGapCell
                          ? 'bg-red-600/5 cursor-pointer hover:bg-red-600/10 transition-colors'
                          : 'group/cell'
                      }`}
                      onClick={isGapCell ? () => {
                        const start = new Date(day)
                        start.setHours(block.hour, 0, 0, 0)
                        const end = new Date(start)
                        end.setHours(block.hour === 20 ? 24 : block.hour + 4, 0, 0, 0)
                        setAssignCellStart(start)
                        setAssignCellEnd(end)
                        setShowAssignModal(true)
                      } : undefined}
                    >
                      {shift ? (
                        <div className="relative">
                          <div
                            className={`text-[10px] p-1 rounded border ${tierColor(shift.tier)}`}
                            title={`${shift.employee?.firstName || 'Unassigned'} ${shift.employee?.lastName || ''} - ${shift.tier}`}
                          >
                            <div className="flex items-center justify-between">
                              <span className="truncate">
                                {shift.employee?.firstName?.charAt(0)}.{shift.employee?.lastName}
                                {shift.status === 'gap' && (
                                  <span className="ml-1 text-red-400">(gap)</span>
                                )}
                              </span>
                              {shift.status !== 'gap' && shift.tier !== 'tertiary' && (
                                <button
                                  onClick={(e) => {
                                    e.stopPropagation()
                                    setSwapTargetShift(shift)
                                    setShowSwapModal(true)
                                  }}
                                  className="opacity-0 group-hover/cell:opacity-100 p-0.5 hover:bg-gray-700 rounded transition-all ml-1"
                                  title="Request swap"
                                >
                                  <Repeat className="w-3 h-3" />
                                </button>
                              )}
                            </div>
                          </div>
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

      {/* Assign Shift Modal */}
      {showAssignModal && selectedSchedule && assignCellStart && assignCellEnd && (
        <AssignShiftModal
          defaultStart={assignCellStart}
          defaultEnd={assignCellEnd}
          onAssign={handleAssignShift}
          onClose={() => {
            setShowAssignModal(false)
            setAssignCellStart(null)
            setAssignCellEnd(null)
          }}
        />
      )}

      {/* Swap Request Modal */}
      {showSwapModal && swapTargetShift && (
        <SwapModal
          shift={swapTargetShift}
          onRequest={(replacementUserId, reason) =>
            handleRequestSwap(swapTargetShift.id, replacementUserId, reason)
          }
          onClose={() => {
            setShowSwapModal(false)
            setSwapTargetShift(null)
          }}
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
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4 max-h-[90vh] overflow-y-auto"
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

function SwapModal({
  shift,
  onRequest,
  onClose,
}: {
  shift: Shift
  onRequest: (replacementUserId: string, reason: string) => Promise<void>
  onClose: () => void
}) {
  const [employees, setEmployees] = useState<Employee[]>([])
  const [loadingEmployees, setLoadingEmployees] = useState(true)
  const [search, setSearch] = useState('')
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const [reason, setReason] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (shift.schedule?.departmentId) {
      directoryApi.search('', shift.schedule.departmentId)
        .then(data => { setEmployees(data); setLoadingEmployees(false) })
        .catch(() => setLoadingEmployees(false))
    } else {
      directoryApi.search('')
        .then(data => { setEmployees(data); setLoadingEmployees(false) })
        .catch(() => setLoadingEmployees(false))
    }
  }, [shift.schedule?.departmentId])

  const filtered = search
    ? employees.filter(e =>
        `${e.firstName} ${e.lastName}`.toLowerCase().includes(search.toLowerCase()) ||
        e.email.toLowerCase().includes(search.toLowerCase())
      )
    : employees

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!selectedEmployee) { setError('Please select a replacement.'); return }
    setSubmitting(true)
    setError(null)
    try {
      await onRequest(selectedEmployee.id, reason)
    } catch {
      setError('Failed to submit swap request.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-md mx-4 max-h-[90vh] overflow-y-auto"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Request Shift Swap</h2>
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

          <div className="text-sm text-gray-400 bg-gray-800 rounded-lg p-3">
            <p><span className="text-gray-500">Shift:</span> {shift.tier} — {formatDateOnly(shift.startTime)} {formatTimeRange(shift.startTime, shift.endTime)}</p>
            {shift.employee && <p><span className="text-gray-500">Currently assigned:</span> {shift.employee.firstName} {shift.employee.lastName}</p>}
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Replacement (who should take this shift?)</label>
            {selectedEmployee ? (
              <div className="flex items-center justify-between bg-gray-800 border border-gray-700 rounded-lg px-4 py-2">
                <div className="flex items-center gap-2">
                  <div className="w-8 h-8 rounded-full bg-gray-700 flex items-center justify-center text-xs font-medium">
                    {selectedEmployee.firstName?.charAt(0)}{selectedEmployee.lastName?.charAt(0)}
                  </div>
                  <div>
                    <p className="text-sm">{selectedEmployee.firstName} {selectedEmployee.lastName}</p>
                    <p className="text-xs text-gray-500">{selectedEmployee.title || selectedEmployee.email}</p>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setSelectedEmployee(null)}
                  className="text-xs text-amber-500 hover:text-amber-400"
                >
                  Change
                </button>
              </div>
            ) : (
              <div className="space-y-2">
                <input
                  type="text"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder="Search by name or email..."
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
                  autoFocus
                />
                <div className="max-h-40 overflow-y-auto space-y-1">
                  {loadingEmployees ? (
                    <div className="flex items-center justify-center py-4">
                      <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-amber-600" />
                    </div>
                  ) : filtered.length === 0 ? (
                    <p className="text-sm text-gray-500 text-center py-2">No employees found</p>
                  ) : (
                    filtered.map((emp) => (
                      <button
                        key={emp.id}
                        type="button"
                        onClick={() => {
                          setSelectedEmployee(emp)
                          setSearch('')
                        }}
                        className="w-full text-left flex items-center gap-3 px-3 py-2 rounded-lg hover:bg-gray-800 transition-colors"
                      >
                        <div className="w-8 h-8 rounded-full bg-gray-700 flex items-center justify-center text-xs font-medium flex-shrink-0">
                          {emp.firstName?.charAt(0)}{emp.lastName?.charAt(0)}
                        </div>
                        <div className="min-w-0">
                          <p className="text-sm truncate">{emp.firstName} {emp.lastName}</p>
                          <p className="text-xs text-gray-500 truncate">{emp.title || emp.email}</p>
                        </div>
                      </button>
                    ))
                  )}
                </div>
              </div>
            )}
          </div>

          <div>
            <label className="block text-sm text-gray-500 mb-1">Reason (optional)</label>
            <textarea
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              rows={2}
              placeholder="e.g., Scheduling conflict, personal time"
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
              disabled={submitting || !selectedEmployee}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
            >
              {submitting ? (
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
              ) : (
                <Repeat className="w-4 h-4" />
              )}
              {submitting ? 'Submitting...' : 'Request Swap'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

function AssignShiftModal({
  defaultStart,
  defaultEnd,
  onAssign,
  onClose,
}: {
  defaultStart: Date
  defaultEnd: Date
  onAssign: (employeeId: string, startTime: string, endTime: string, tier: string) => Promise<void>
  onClose: () => void
}) {
  const [employees, setEmployees] = useState<Employee[]>([])
  const [loadingEmployees, setLoadingEmployees] = useState(true)
  const [search, setSearch] = useState('')
  const [selectedEmployee, setSelectedEmployee] = useState<Employee | null>(null)
  const [startTime, setStartTime] = useState(
    defaultStart.toLocaleString('sv-SE', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).replace(' ', 'T')
  )
  const [endTime, setEndTime] = useState(
    defaultEnd.toLocaleString('sv-SE', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).replace(' ', 'T')
  )
  const [tier, setTier] = useState<'primary' | 'secondary' | 'tertiary'>('primary')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    directoryApi.search('')
      .then(data => {
        setEmployees(data)
        setLoadingEmployees(false)
      })
      .catch(() => setLoadingEmployees(false))
  }, [])

  const filtered = search
    ? employees.filter(e =>
        `${e.firstName} ${e.lastName}`.toLowerCase().includes(search.toLowerCase()) ||
        e.email.toLowerCase().includes(search.toLowerCase()) ||
        e.title?.toLowerCase().includes(search.toLowerCase())
      )
    : employees

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!selectedEmployee) { setError('Please select an employee.'); return }
    if (!startTime || !endTime) { setError('Start and end times are required.'); return }
    if (new Date(endTime) <= new Date(startTime)) { setError('End time must be after start time.'); return }

    setSaving(true)
    setError(null)
    try {
      await onAssign(
        selectedEmployee.id,
        new Date(startTime).toISOString(),
        new Date(endTime).toISOString(),
        tier,
      )
    } catch {
      setError('Failed to assign shift.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4 max-h-[90vh] overflow-y-auto"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Assign Shift</h2>
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

          {/* Employee Search */}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Employee</label>
            {selectedEmployee ? (
              <div className="flex items-center justify-between bg-gray-800 border border-gray-700 rounded-lg px-4 py-2">
                <div className="flex items-center gap-2">
                  <div className="w-8 h-8 rounded-full bg-gray-700 flex items-center justify-center text-xs font-medium">
                    {selectedEmployee.firstName?.charAt(0)}{selectedEmployee.lastName?.charAt(0)}
                  </div>
                  <div>
                    <p className="text-sm">{selectedEmployee.firstName} {selectedEmployee.lastName}</p>
                    <p className="text-xs text-gray-500">{selectedEmployee.title || selectedEmployee.email}</p>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setSelectedEmployee(null)}
                  className="text-xs text-amber-500 hover:text-amber-400"
                >
                  Change
                </button>
              </div>
            ) : (
              <div className="space-y-2">
                <input
                  type="text"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                  placeholder="Search by name, email, or title..."
                  className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
                  autoFocus
                />
                <div className="max-h-48 overflow-y-auto space-y-1">
                  {loadingEmployees ? (
                    <div className="flex items-center justify-center py-4">
                      <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-amber-600" />
                    </div>
                  ) : filtered.length === 0 ? (
                    <p className="text-sm text-gray-500 text-center py-2">No employees found</p>
                  ) : (
                    filtered.map((emp) => (
                      <button
                        key={emp.id}
                        type="button"
                        onClick={() => {
                          setSelectedEmployee(emp)
                          setSearch('')
                        }}
                        className="w-full text-left flex items-center gap-3 px-3 py-2 rounded-lg hover:bg-gray-800 transition-colors"
                      >
                        <div className="w-8 h-8 rounded-full bg-gray-700 flex items-center justify-center text-xs font-medium flex-shrink-0">
                          {emp.firstName?.charAt(0)}{emp.lastName?.charAt(0)}
                        </div>
                        <div className="min-w-0">
                          <p className="text-sm truncate">{emp.firstName} {emp.lastName}</p>
                          <p className="text-xs text-gray-500 truncate">{emp.title || emp.email}</p>
                        </div>
                      </button>
                    ))
                  )}
                </div>
              </div>
            )}
          </div>

          {/* Time Range */}
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm text-gray-500 mb-1">Start Time</label>
              <input
                type="datetime-local"
                required
                value={startTime}
                onChange={(e) => setStartTime(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-500 mb-1">End Time</label>
              <input
                type="datetime-local"
                required
                value={endTime}
                onChange={(e) => setEndTime(e.target.value)}
                className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
              />
            </div>
          </div>

          {/* Tier */}
          <div>
            <label className="block text-sm text-gray-500 mb-1">Tier</label>
            <select
              value={tier}
              onChange={(e) => setTier(e.target.value as typeof tier)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
            >
              <option value="primary">Primary</option>
              <option value="secondary">Secondary</option>
              <option value="tertiary">Tertiary</option>
            </select>
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
              disabled={saving || !selectedEmployee}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
            >
              {saving ? (
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
              ) : (
                <Save className="w-4 h-4" />
              )}
              {saving ? 'Assigning...' : 'Assign Shift'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
