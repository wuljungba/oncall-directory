import { useState, useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { Clock, Phone, Users, AlertTriangle, MessageSquare, Mail } from 'lucide-react'
import { scheduleApi, directoryApi } from '@/services/api'
import { useAuth } from '@/hooks/useAuth'
import { useSignalR } from '@/hooks/useSignalR'
import { Card } from '@/components/ui/Card'
import { Stat } from '@/components/ui/Stat'
import { Badge } from '@/components/ui/Badge'
import { presenceDotClass, presenceLabel } from '@/utils/presence'
import { formatTimeRange, formatCountdown } from '@/utils/date'
import type { Employee, Shift, TimeOff } from '@/types'

interface OnCallSummary {
  employeeName: string
  role: string
  department: string
  tier: string
  startTime: string
  endTime: string
  presence: string
  email?: string
  phone?: string
}

export default function Dashboard() {
  const { employeeId } = useAuth()
  const [onCallNow, setOnCallNow] = useState<OnCallSummary[]>([])
  const [rawShifts, setRawShifts] = useState<Shift[]>([])
  const [stats, setStats] = useState({ onCall: 0, departments: 0, employees: 0 })
  const [now, setNow] = useState(new Date())
  const [myProfile, setMyProfile] = useState<Employee | null>(null)
  const [myTimeOff, setMyTimeOff] = useState<TimeOff[]>([])
  const { lastEvent } = useSignalR()
  const loadTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  // Live countdown tick.
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(t)
  }, [])

  async function loadDashboard() {
    try {
      const [shifts, employees] = await Promise.all([
        scheduleApi.getOnCall(),
        directoryApi.search(''),
      ])

      const summaries = shifts.map((s: Shift) => ({
        employeeName: `${s.employee?.firstName} ${s.employee?.lastName}`,
        role: s.employee?.title || 'Unknown',
        department: s.employee?.department?.name || '',
        tier: s.tier,
        startTime: s.startTime,
        endTime: s.endTime,
        presence: s.employee?.presence || 'unknown',
        email: s.employee?.email,
        phone: s.employee?.officePhone || s.employee?.mobilePhone,
      }))

      setRawShifts(shifts)
      setOnCallNow(summaries)
      setStats({
        onCall: shifts.length,
        departments: new Set(employees.map((e: Employee) => e.departmentId)).size,
        employees: employees.length,
      })
    } catch (err) {
      console.error('Failed to load dashboard:', err)
    }
  }

  // My coverage: profile + my time-off requests.
  useEffect(() => {
    if (!employeeId) return
    Promise.all([directoryApi.get(employeeId), scheduleApi.getMyTimeOff()])
      .then(([prof, reqs]) => { setMyProfile(prof); setMyTimeOff(reqs) })
      .catch(() => { /* ignore */ })
  }, [employeeId])

  // Initial load
  useEffect(() => {
    loadDashboard()
  }, [])

  // Real-time updates via SignalR
  useEffect(() => {
    if (!lastEvent) return

    // Debounce rapid events — only re-fetch once within 300ms
    if (loadTimerRef.current) {
      clearTimeout(loadTimerRef.current)
    }

    const relevantEvents = [
      'ScheduleCreated', 'ScheduleUpdated', 'ScheduleDeleted',
      'ShiftAssigned', 'ShiftsGenerated',
      'SwapRequested', 'SwapApproved',
      'TimeOffUpdated',
      'EmployeeCreated', 'EmployeeUpdated', 'EmployeeDeactivated',
      'DepartmentCreated', 'DepartmentUpdated', 'DepartmentDeactivated',
    ]

    if (relevantEvents.includes(lastEvent.type)) {
      loadTimerRef.current = setTimeout(() => {
        loadDashboard()
      }, 300)
    }

    return () => {
      if (loadTimerRef.current) {
        clearTimeout(loadTimerRef.current)
      }
    }
  }, [lastEvent])

  const myOnCall = rawShifts.find(s => s.employee?.id === employeeId)
  const pendingCount = myTimeOff.filter(r => r.status === 'pending').length
  const approvedCount = myTimeOff.filter(r => r.status === 'approved').length

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold">Dashboard</h1>

      {/* Stats Cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Stat label="Currently On Call" value={stats.onCall} tone="amber" icon={<Clock className="w-10 h-10" />} />
        <Stat label="Departments Covering" value={stats.departments} tone="blue" icon={<Users className="w-10 h-10" />} />
        <Stat label="Directory Entries" value={stats.employees} tone="green" icon={<Phone className="w-10 h-10" />} />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Currently On Call */}
        <div className="lg:col-span-2">
          <Card title="Currently On Call">
            <div aria-live="polite">
              {onCallNow.length === 0 ? (
                <div className="flex items-center gap-3 text-gray-500 py-8 justify-center">
                  <AlertTriangle className="w-5 h-5" />
                  <p>No one is currently on call</p>
                </div>
              ) : (
                <div className="space-y-3">
                  {onCallNow.map((person, i) => (
                    <div
                      key={i}
                      className="flex items-center justify-between p-3 bg-gray-800/50 rounded-lg"
                    >
                      <div className="flex items-center gap-3 min-w-0">
                        <div
                          className={`w-2 h-2 rounded-full flex-shrink-0 ${presenceDotClass(person.presence)}`}
                          title={presenceLabel(person.presence)}
                        />
                        <div className="min-w-0">
                          <p className="text-sm font-medium truncate">
                            {person.employeeName}
                            <span className="text-xs text-gray-500 ml-2">{presenceLabel(person.presence)}</span>
                          </p>
                          <p className="text-xs text-gray-500 truncate">
                            {person.role} — {person.department}
                          </p>
                        </div>
                      </div>
                      <div className="flex items-center gap-3 flex-shrink-0">
                        {/* Always-visible actions */}
                        <div className="hidden sm:flex items-center gap-1">
                          {person.phone && (
                            <a href={`tel:${person.phone}`} title="Call" className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors">
                              <Phone className="w-4 h-4 text-gray-400 hover:text-amber-400" />
                            </a>
                          )}
                          {person.email && (
                            <a href={`mailto:${person.email}`} title="Email" className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors">
                              <Mail className="w-4 h-4 text-gray-400 hover:text-amber-400" />
                            </a>
                          )}
                          {person.email && (
                            <a
                              href={`https://teams.microsoft.com/l/chat/0/0?users=${encodeURIComponent(person.email)}`}
                              target="_blank" rel="noopener noreferrer"
                              title="Chat in Teams" className="p-1.5 hover:bg-gray-700 rounded-lg transition-colors"
                            >
                              <MessageSquare className="w-4 h-4 text-gray-400 hover:text-blue-400" />
                            </a>
                          )}
                        </div>
                        <div className="text-right">
                          <Badge tone={person.tier === 'primary' ? 'amber' : person.tier === 'secondary' ? 'blue' : 'gray'}>
                            {person.tier}
                          </Badge>
                          <p className="text-xs text-gray-500 mt-1">{formatTimeRange(person.startTime, person.endTime)}</p>
                          <p className="text-xs text-amber-500">{formatCountdown(person.endTime, now)}</p>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </Card>
        </div>

        {/* My Coverage */}
        <Card title="My Coverage">
          {!employeeId ? (
            <p className="text-sm text-gray-500 py-6 text-center">No employee profile is linked to your account.</p>
          ) : !myProfile ? (
            <div className="flex justify-center py-10"><div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" /></div>
          ) : (
            <div className="space-y-4">
              <div className="flex items-center gap-3">
                <div className="w-12 h-12 rounded-full bg-amber-600 flex items-center justify-center text-lg font-medium">
                  {myProfile.firstName?.charAt(0)}{myProfile.lastName?.charAt(0)}
                </div>
                <div className="min-w-0">
                  <p className="text-sm font-medium truncate">{myProfile.firstName} {myProfile.lastName}</p>
                  <p className="text-xs text-gray-500 truncate">
                    {myProfile.title}{myProfile.department ? ` · ${myProfile.department.name}` : ''}
                  </p>
                  <p className="text-xs text-gray-500 mt-0.5 flex items-center gap-1.5">
                    <span className={`w-2 h-2 rounded-full ${presenceDotClass(myProfile.presence)}`} />
                    {presenceLabel(myProfile.presence)}
                  </p>
                </div>
              </div>

              <div className="grid grid-cols-2 gap-2">
                <div className="bg-gray-800/60 rounded-lg p-3 text-center">
                  <p className="text-2xl font-bold text-amber-500">{pendingCount}</p>
                  <p className="text-xs text-gray-500">Pending time off</p>
                </div>
                <div className="bg-gray-800/60 rounded-lg p-3 text-center">
                  <p className="text-2xl font-bold text-green-500">{approvedCount}</p>
                  <p className="text-xs text-gray-500">Approved requests</p>
                </div>
              </div>

              {myOnCall ? (
                <p className="text-xs text-gray-400 bg-gray-800/50 rounded-lg px-3 py-2">
                  <span className="text-amber-500 font-medium">On call now</span> ·{' '}
                  {formatTimeRange(myOnCall.startTime, myOnCall.endTime)} · {formatCountdown(myOnCall.endTime, now)}
                </p>
              ) : (
                <p className="text-xs text-gray-600 bg-gray-800/50 rounded-lg px-3 py-2">Not currently on call</p>
              )}
            </div>
          )}
        </Card>
      </div>

      {/* Quick Actions */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <Link
          to="/dashboard/schedule"
          className="bg-gray-900 border border-gray-800 rounded-xl p-5 hover:border-amber-600/50 transition-colors block"
        >
          <CalendarIcon />
          <h3 className="font-medium mt-2">View Schedule</h3>
          <p className="text-sm text-gray-500 mt-1">
            See upcoming on-call rotations
          </p>
        </Link>
        <Link
          to="/dashboard/directory"
          className="bg-gray-900 border border-gray-800 rounded-xl p-5 hover:border-amber-600/50 transition-colors block"
        >
          <PhoneIcon />
          <h3 className="font-medium mt-2">Phone Directory</h3>
          <p className="text-sm text-gray-500 mt-1">
            Find colleagues and contact info
          </p>
        </Link>
        <Link
          to="/dashboard/time-off"
          className="bg-gray-900 border border-gray-800 rounded-xl p-5 hover:border-amber-600/50 transition-colors block"
        >
          <CalendarDaysIcon />
          <h3 className="font-medium mt-2">Request Time Off</h3>
          <p className="text-sm text-gray-500 mt-1">
            Submit blackout dates for scheduling
          </p>
        </Link>
      </div>
    </div>
  )
}

// Simple icons (avoiding naming conflicts with lucide)
function CalendarIcon() {
  return (
    <svg className="w-8 h-8 text-amber-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
    </svg>
  )
}

function PhoneIcon() {
  return (
    <svg className="w-8 h-8 text-blue-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M3 5a2 2 0 012-2h3.28a1 1 0 01.948.684l1.498 4.493a1 1 0 01-.502 1.21l-2.257 1.13a11.042 11.042 0 005.516 5.516l1.13-2.257a1 1 0 011.21-.502l4.493 1.498a1 1 0 01.684.949V19a2 2 0 01-2 2h-1C9.716 21 3 14.284 3 6V5z" />
    </svg>
  )
}

function CalendarDaysIcon() {
  return (
    <svg className="w-8 h-8 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M8 7V3m8 4V3m-9 8h10m-6 4h.01M12 17h.01M9 20h6m-7-4h.01M15 16h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
    </svg>
  )
}