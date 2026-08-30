import { useState, useEffect, useRef, useCallback } from 'react'
import { AlertTriangle, CheckCircle, Play, X, PhoneCall } from 'lucide-react'
import { commandCenterApi, phoneTreesApi, codeCallLocationsApi, departmentsApi } from '@/services/api'
import { useSignalR } from '@/hooks/useSignalR'
import type { PhoneTreeEvent, PhoneTree, CodeCallLocation, Department } from '@/types'

// Descriptions for the codes most hospitals share. This is no longer the list of codes
// you may raise -- that comes from the tenant's own phone trees, so a site can define
// whatever its operation calls for. These only supply familiar wording when the type
// happens to be one of them.
const CODE_TYPE_HINTS: Record<string, string> = {
  'code-blue': 'Cardiac/respiratory arrest',
  'code-red': 'Smoke / fire detected',
  'code-green': 'Facility evacuation',
  'code-silver': 'Armed person / lockdown',
  'code-grey': 'Weather emergency',
  'code-pink': 'Nursery / peds security',
}

// Keys must match the StepKey values emitted by CodeCallDispatchService — a mismatch
// leaves the step stuck on "pending" forever.
const PIPELINE_STEPS = [
  { key: 'created', label: 'Event Created' },
  { key: 'cucm_axl_check', label: 'CUCM Device Check' },
  { key: 'informacast', label: 'InformaCast Broadcast' },
  { key: 'vocera', label: 'Vocera Alert' },
  { key: 'twilio_sms', label: 'SMS to On-Call' },
  { key: 'acknowledged', label: 'Team Acknowledged' },
]

function formatElapsed(start: string, end?: string | null): string {
  const s = Math.floor(((end ? new Date(end).getTime() : Date.now()) - new Date(start).getTime()) / 1000)
  const m = Math.floor(s / 60)
  const rem = s % 60
  return `${m}:${rem.toString().padStart(2, '0')}`
}

export default function CommandCenterPage() {
  const [activeIncidents, setActiveIncidents] = useState<PhoneTreeEvent[]>([])
  const [resolvedIncidents, setResolvedIncidents] = useState<PhoneTreeEvent[]>([])
  const [phoneTrees, setPhoneTrees] = useState<PhoneTree[]>([])
  const [locations, setLocations] = useState<CodeCallLocation[]>([])
  const [loading, setLoading] = useState(true)
  const [showActivateModal, setShowActivateModal] = useState(false)
  const [logEntries, setLogEntries] = useState<{ time: string; tag: string; text: string }[]>([])
  const [clock, setClock] = useState('')
  const [completing, setCompleting] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [showArchived, setShowArchived] = useState(false)
  const [debriefNotes, setDebriefNotes] = useState<Record<number, string>>({})
  const { lastEvent } = useSignalR()
  const prevEventRef = useRef<string | null>(null)
  const logRef = useRef<HTMLDivElement>(null)

  // Live clock
  useEffect(() => {
    const tick = () => setClock(new Date().toLocaleTimeString('en-US', { hour12: false }))
    tick()
    const id = setInterval(tick, 1000)
    return () => clearInterval(id)
  }, [])

  // Live elapsed timers
  const [, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 1000)
    return () => clearInterval(id)
  }, [])

  useEffect(() => { loadData() }, [])

  // SignalR live updates
  useEffect(() => {
    if (!lastEvent) return
    const eventKey = `${lastEvent.type}-${JSON.stringify(lastEvent.payload)}`
    if (eventKey === prevEventRef.current) return
    prevEventRef.current = eventKey
    if (['IncidentCreated', 'IncidentUpdated', 'IncidentResolved', 'DispatchStepCompleted'].includes(lastEvent.type)) {
      loadData()
      if (lastEvent.type === 'DispatchStepCompleted') {
        // Mirrors the anonymous broadcast shape from CodeCallDispatchService.BroadcastStepUpdate.
        const p = lastEvent.payload as { stepKey: string; status: string; detail?: string }
        addLogEntry('dispatch', `Step "${p.stepKey}" ${p.status} — ${p.detail || ''}`)
      }
    }
  }, [lastEvent])

  async function loadData() {
    try {
      const [active, resolved, trees, locs] = await Promise.all([
        commandCenterApi.getActive(),
        commandCenterApi.getResolved(),
        phoneTreesApi.getAll(),
        codeCallLocationsApi.getAll(),
      ])
      setActiveIncidents(active)
      setResolvedIncidents(resolved)
      setPhoneTrees(trees)
      setLocations(locs)
      // Initialize debrief notes from resolved incidents
      const notes: Record<number, string> = {}
      resolved.forEach((inc: PhoneTreeEvent) => { if (inc.debriefNotes) notes[inc.id] = inc.debriefNotes })
      setDebriefNotes(prev => ({ ...prev, ...notes }))
    } catch { /* ignore */ }
    setLoading(false)
  }

  function addLogEntry(tag: string, text: string) {
    const time = new Date().toLocaleTimeString('en-US', { hour12: false })
    setLogEntries(prev => [{ time, tag, text }, ...prev].slice(0, 100))
  }

  /**
   * Adds a code type for this tenant. A code IS a phone tree, so defining one here is the
   * same act as creating the tree, and it needs a department: a tree without one belongs
   * to no tenant, which makes it invisible to its own author and stops its live updates
   * being broadcast.
   */
  async function handleCreateCodeType(name: string, treeType: string, departmentId: number) {
    const tree = await phoneTreesApi.create({
      name, treeType, departmentId,
      procedure: `Response procedure for ${name}. Add the people to call under Phone Trees.`,
    })
    setPhoneTrees(prev => [...prev, tree])
    addLogEntry('info', `Added code type "${name}"`)
    return tree
  }

  async function handleActivate(codeType: string, location: string, notes: string, requestedByName = '') {
    // Codes are the tenant's own trees, so the selected one exists. It used to be
    // manufactured on the spot when missing -- an empty tree, with no department and
    // nobody in it, created during an emergency and indistinguishable from a real one.
    const tree = phoneTrees.find(t => t.treeType === codeType)
    if (!tree) {
      addLogEntry('info', `Could not activate: no code call configured for ${codeType}.`)
      return
    }
    try {
      const evt = await phoneTreesApi.createEvent(tree.id, {
        startedAt: new Date().toISOString(),
        location,
        notes: notes || undefined,
        requestedByName: requestedByName.trim() || undefined,
        confirm: true,
      })
      addLogEntry('dispatch', `Incident #${evt.id} created — ${codeType} @ ${location}${requestedByName.trim() ? ` — reported by ${requestedByName.trim()}` : ''}`)
      setShowActivateModal(false)
    } catch (err) {
      addLogEntry('info', `Failed to create incident: ${err}`)
    }
  }

  async function handleResolve(eventId: number) {
    const notified = window.prompt('Person(s) notified after dispatch (optional):', '') ?? ''
    setCompleting(eventId)
    try {
      await commandCenterApi.resolveEvent(eventId, { notifiedByName: notified || undefined })
      addLogEntry('info', `Incident #${eventId} marked resolved${notified.trim() ? ` — notified: ${notified.trim()}` : ''}`)
      // Reload rather than relying on the IncidentResolved SignalR broadcast: if the hub
      // is not connected the incident stays in the Active column and the resolve looks
      // like it silently did nothing, even though the server completed it.
      await loadData()
    } catch (err) {
      // Never swallow this. An operator who cannot tell whether a code call was closed
      // out will either leave it open or resolve it twice.
      addLogEntry('info', `Failed to resolve incident #${eventId}: ${err}`)
    }
    setCompleting(null)
  }

  const filteredResolved = resolvedIncidents.filter(inc => matchesSearch(inc, search) && (showArchived || !isArchived(inc)))

  const saveDebriefNote = useCallback(async (eventId: number, notes: string) => {
    await commandCenterApi.saveDebriefNotes(eventId, notes)
    setDebriefNotes(prev => ({ ...prev, [eventId]: notes }))
  }, [])

  function fmt(s?: string) {
  return s
    ? new Date(s).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false })
    : '—'
}

/** How long the incident ran, for the line under its start time. */
function durationLabel(inc: PhoneTreeEvent) {
  if (!inc.endedAt) return 'ongoing'
  const ms = new Date(inc.endedAt).getTime() - new Date(inc.startedAt).getTime()
  if (!Number.isFinite(ms) || ms < 0) return ''
  const mins = Math.round(ms / 60000)
  return mins < 1 ? 'under a minute' : mins === 1 ? '1 minute' : `${mins} minutes`
}

/** Seconds to acknowledgement, as m:ss. */
function responseLabel(seconds?: number) {
  if (!seconds && seconds !== 0) return null
  const m = Math.floor(seconds / 60)
  const sec = String(seconds % 60).padStart(2, '0')
  return `${m}:${sec}`
}

/**
 * A colour per code, so a row is identifiable before it is read. Codes a tenant defines
 * itself fall through to the neutral accent rather than borrowing another code's meaning.
 */
function codeAccent(treeType?: string) {
  switch (treeType) {
    case 'code-blue': return 'bg-blue-500'
    case 'code-red': return 'bg-red-500'
    case 'code-green': return 'bg-green-500'
    case 'code-silver': return 'bg-slate-400'
    case 'code-grey': return 'bg-gray-400'
    case 'code-pink': return 'bg-pink-400'
    default: return 'bg-amber-500'
  }
}

/** Operator who triggered the code — pinned signed-in name, else resolved employee name. */
function trigName(inc: PhoneTreeEvent) {
  if (inc.initiatedByName) return inc.initiatedByName
  if (inc.initiatedBy) return `${inc.initiatedBy.firstName} ${inc.initiatedBy.lastName}`.trim()
  return ''
}

function isArchived(inc: PhoneTreeEvent) {
  // Local const (not module scope) so no bundler/minifier ordering can ever put this
  // in a temporal dead zone for a hoisted helper.
  const archiveDays = 90
  const cut = Date.now() - archiveDays * 24 * 60 * 60 * 1000
  return new Date(inc.endedAt || inc.startedAt).getTime() < cut
}

function matchesSearch(inc: PhoneTreeEvent, q: string) {
  const s = q.trim().toLowerCase()
  if (!s) return true
  const code = inc.phoneTree?.name || inc.phoneTree?.treeType || ''
  return `${inc.id} ${inc.location || ''} ${code} ${inc.requestedByName || ''} ${inc.initiatedByName || ''} ${trigName(inc)} ${inc.notifiedByName || ''} ${inc.outcome || ''}`.toLowerCase().includes(s)
}

/**
 * A dispatch step's state, for the chip that represents it.
 *
 * 'skipped' had no case here and fell through to 'active', so a channel that was switched
 * off rendered exactly like one mid-page -- amber and pulsing, indefinitely. A code call
 * that reached nobody was therefore indistinguishable from one in progress.
 */
function getStepStatus(evt: PhoneTreeEvent, stepKey: string) {
    if (evt.status === 'completed') return 'done'
    const step = evt.dispatchSteps?.find(s => s.stepKey === stepKey)
    if (!step) return stepKey === 'created' ? 'done' : 'pending'
    if (step.status === 'completed') return 'done'
    if (step.status === 'failed') return 'failed'
    if (step.status === 'skipped') return 'skipped'
    return 'active'
  }

/** The server's explanation for a step, e.g. why a channel was skipped. */
function stepDetail(evt: PhoneTreeEvent, stepKey: string) {
  return evt.dispatchSteps?.find(s => s.stepKey === stepKey)?.detail
}

/**
 * The failure the operator most needs and could not see: dispatch that contacted nobody.
 * The server records it on the steps; nothing rendered it.
 */
function dispatchReachedNobody(evt: PhoneTreeEvent) {
  const steps = evt.dispatchSteps ?? []
  const channels = steps.filter(s => s.stepKey !== 'created' && s.stepKey !== 'acknowledged')
  if (channels.length === 0) return false
  return channels.every(s => s.status === 'skipped' || s.status === 'failed')
}

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-9 h-9 rounded-lg bg-gradient-to-br from-red-600 to-amber-500 flex items-center justify-center text-sm font-bold text-gray-950">
            CC
          </div>
          <div>
            <h1 className="text-lg font-bold">Code Call Command Center</h1>
            <p className="text-xs text-gray-500 font-mono uppercase tracking-wider">Dispatch · InformaCast · SIP Trunk Paging</p>
          </div>
        </div>
        <div className="flex items-center gap-4">
          <span className="font-mono text-sm text-gray-500">{clock}</span>
          <button
            onClick={() => setShowActivateModal(true)}
            className="flex items-center gap-2 px-5 py-2.5 bg-red-600 hover:bg-red-700 rounded-lg text-sm font-bold transition-colors shadow-lg shadow-red-600/25 animate-pulse"
          >
            <AlertTriangle className="w-4 h-4" />
            Activate Code
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Active Incidents Panel */}
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between">
            <h2 className="text-xs font-bold text-gray-500 uppercase tracking-wider">Active & Recent Incidents</h2>
            <span className="font-mono text-xs bg-gray-800 px-2 py-0.5 rounded-full text-gray-500">
              {activeIncidents.filter(i => i.status === 'active').length} active
            </span>
          </div>
          <div className="p-4 space-y-3 max-h-[500px] overflow-y-auto">
            {loading ? (
              <div className="flex justify-center py-12"><div className="animate-spin rounded-full h-6 w-6 border-b-2 border-amber-600" /></div>
            ) : activeIncidents.length === 0 ? (
              <div className="flex flex-col items-center py-12 text-gray-500">
                <PhoneCall className="w-10 h-10 mb-3 text-gray-700" />
                <p className="text-sm">No active incidents</p>
                <p className="text-xs mt-1 text-gray-600">Facility status normal</p>
              </div>
            ) : (
              activeIncidents.map(inc => (
                <div key={inc.id} className={`bg-gray-800/50 border border-gray-700 rounded-xl p-4 ${inc.status === 'completed' ? 'opacity-60' : 'border-l-4 border-l-red-600'}`}>
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <p className="text-sm font-bold">#{inc.id} — {inc.phoneTree?.name || inc.phoneTree?.treeType || 'Code Call'}</p>
                      <p className="text-xs text-gray-500 mt-0.5">
                        {inc.location && <span className="capitalize">{inc.location}</span>}
                        {inc.requestedByName ? <span> · Reported by <span className="text-gray-300">{inc.requestedByName}</span></span> : ''}
                        {trigName(inc) ? <span> · Triggered by <span className="text-gray-300">{trigName(inc)}</span></span> : ''}
                        {inc.notifiedByName ? <span> · Notified: <span className="text-gray-300">{inc.notifiedByName}</span></span> : ''}
                        <span> · {fmt(inc.startedAt)}{inc.endedAt ? <> – {fmt(inc.endedAt)}</> : ' (active)'}</span>
                      </p>
                      {inc.notes && <p className="text-xs text-gray-600 mt-0.5">{inc.notes}</p>}
                      {inc.participants && inc.participants.length > 0 && (
                        <p className="text-xs text-gray-500 mt-0.5 truncate" title={inc.participants.map(p => p.employee ? `${p.employee.firstName} ${p.employee.lastName}` : (p.role || '—')).join(', ')}>
                          Notified: {inc.participants.map(p => p.employee ? `${p.employee.firstName} ${p.employee.lastName}` : (p.role || '—')).join(', ')}
                        </p>
                      )}
                    </div>
                    <div className={`font-mono text-xl font-bold ${inc.status === 'completed' ? 'text-green-500' : 'text-red-500'}`}>
                      {formatElapsed(inc.startedAt, inc.endedAt)}
                    </div>
                  </div>
                  {/* Pipeline status chips */}
                  <div className="flex flex-wrap gap-1.5 mt-3">
                    {PIPELINE_STEPS.map(step => {
                      const status = getStepStatus(inc, step.key)
                      const detail = stepDetail(inc, step.key)
                      return (
                        <span key={step.key}
                          title={detail || undefined}
                          className={`inline-flex items-center gap-1 font-mono text-[10px] px-2 py-1 rounded-md border ${
                          status === 'done' ? 'bg-green-600/20 text-green-500 border-green-600/30' :
                          status === 'failed' ? 'bg-red-600/20 text-red-500 border-red-600/30' :
                          status === 'skipped' ? 'bg-gray-800/60 text-gray-500 border-gray-700 line-through decoration-gray-600' :
                          status === 'active' ? 'bg-yellow-600/20 text-yellow-500 border-yellow-600/30 animate-pulse' :
                          'bg-gray-800 text-gray-600 border-gray-700'
                        }`}>
                          <span className="w-1.5 h-1.5 rounded-full bg-current" />
                          {step.label}
                          {status === 'skipped' && <span className="not-italic no-underline"> off</span>}
                        </span>
                      )
                    })}
                  </div>

                  {/* The server already decided this and said so on the steps. Rendering it
                      is the difference between an operator escalating by phone and waiting
                      on a page that was never sent. */}
                  {dispatchReachedNobody(inc) && (
                    <div className="flex items-start gap-2 mt-3 text-xs text-red-300 bg-red-600/10 border border-red-600/40 rounded-lg px-3 py-2">
                      <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
                      <span>
                        <strong>Nobody was contacted.</strong> Every dispatch channel was off
                        or failed. Escalate by phone now — this incident has not paged anyone.
                      </span>
                    </div>
                  )}
                  {/* Actions */}
                  <div className="flex gap-2 mt-3">
                    {inc.status === 'active' && (
                      <button
                        onClick={() => handleResolve(inc.id)}
                        disabled={completing === inc.id}
                        className="flex items-center gap-1 text-xs px-3 py-1.5 bg-green-600/20 hover:bg-green-600/30 text-green-500 rounded-lg transition-colors disabled:opacity-50"
                      >
                        <CheckCircle className="w-3.5 h-3.5" />
                        {completing === inc.id ? 'Resolving...' : 'Mark Resolved'}
                      </button>
                    )}
                  </div>
                </div>
              ))
            )}
          </div>
        </div>

        {/* Pipeline Log Feed */}
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between">
            <h2 className="text-xs font-bold text-gray-500 uppercase tracking-wider">Dispatch Pipeline Log</h2>
            <span className="font-mono text-xs bg-gray-800 px-2 py-0.5 rounded-full text-gray-500">live</span>
          </div>
          <div ref={logRef} className="p-4 font-mono text-xs max-h-[500px] overflow-y-auto space-y-1">
            {/* This read "All paging channels nominal" -- a fixed string, not a check of
                anything, reassuring during exactly the failure it could not see. */}
            {logEntries.length === 0 ? (
              <div className="text-gray-600 py-8 text-center">Awaiting activation.</div>
            ) : (
              logEntries.map((entry, i) => (
                <div key={i} className="flex gap-2 text-gray-400 py-1 border-b border-gray-800/50">
                  <span className="text-gray-600 shrink-0">{entry.time}</span>
                  <span className={`shrink-0 px-1.5 py-0.5 rounded text-[9px] font-bold uppercase ${
                    entry.tag === 'dispatch' ? 'bg-blue-600/20 text-blue-400' :
                    entry.tag === 'info' ? 'bg-gray-800 text-gray-400 border border-gray-700' :
                    'bg-amber-600/20 text-amber-400'
                  }`}>
                    {entry.tag}
                  </span>
                  <span>{entry.text}</span>
                </div>
              ))
            )}
          </div>
        </div>
      </div>

      {/* Debrief Log */}
      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800 flex flex-wrap items-center gap-2">
          <h2 className="text-xs font-bold text-gray-500 uppercase tracking-wider">Incident History</h2>
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search by code, location, name, id..."
            className="bg-gray-800 border border-gray-700 rounded px-2 py-1 text-xs focus:outline-none focus:border-amber-600 w-52"
          />
          <label className="flex items-center gap-1.5 text-xs text-gray-500 cursor-pointer">
            <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} />
            Show archived (&gt;90d)
          </label>
          <span className="font-mono text-xs bg-gray-800 px-2 py-0.5 rounded-full text-gray-500">
            {resolvedIncidents.length} total
          </span>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-[10px] text-gray-500 uppercase tracking-wider border-b border-gray-800">
                <th className="px-5 py-3 font-medium">Incident</th>
                <th className="px-5 py-3 font-medium">Location</th>
                <th className="px-5 py-3 font-medium">People</th>
                <th className="px-5 py-3 font-medium">Window</th>
                <th className="px-5 py-3 font-medium text-right">Response</th>
                <th className="px-5 py-3 font-medium">Outcome</th>
                <th className="px-5 py-3 font-medium">Debrief note</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-800/70">
              {filteredResolved.length === 0 ? (
                <tr><td colSpan={7} className="text-center text-gray-600 py-10 text-sm">
                  {resolvedIncidents.length === 0 ? 'No incidents yet.' : 'No incidents match. Try clearing search or showing archived.'}
                </td></tr>
              ) : (
                filteredResolved.map(inc => {
                  const response = responseLabel(inc.responseTimeSeconds)
                  return (
                    <tr key={inc.id} className="hover:bg-gray-800/30 transition-colors align-top">
                      {/* Identity: the colour and the code name carry it, so the raw
                          treeType slug that used to sit in its own column is gone. */}
                      <td className="px-5 py-4">
                        <div className="flex items-start gap-2.5">
                          <span className={`w-1.5 h-1.5 rounded-full mt-1.5 shrink-0 ${codeAccent(inc.phoneTree?.treeType)}`} />
                          <div className="min-w-0">
                            <p className="text-sm text-gray-200 leading-tight">{inc.phoneTree?.name || 'Code Call'}</p>
                            <p className="text-[11px] text-gray-600 font-mono tabular-nums mt-0.5">#{inc.id}</p>
                          </div>
                        </div>
                      </td>

                      <td className="px-5 py-4 text-sm text-gray-400">{inc.location || '—'}</td>

                      {/* Was one run-on sentence; each role now reads on its own line. */}
                      <td className="px-5 py-4">
                        {!inc.requestedByName && !trigName(inc) && !inc.notifiedByName ? (
                          <span className="text-sm text-gray-600">—</span>
                        ) : (
                          <div className="space-y-1">
                            {inc.requestedByName && (
                              <p className="text-xs"><span className="text-gray-600">Reported </span><span className="text-gray-300">{inc.requestedByName}</span></p>
                            )}
                            {trigName(inc) && (
                              <p className="text-xs"><span className="text-gray-600">Triggered </span><span className="text-gray-300">{trigName(inc)}</span></p>
                            )}
                            {inc.notifiedByName && (
                              <p className="text-xs"><span className="text-gray-600">Notified </span><span className="text-gray-300">{inc.notifiedByName}</span></p>
                            )}
                          </div>
                        )}
                      </td>

                      <td className="px-5 py-4">
                        <p className="text-sm text-gray-300 font-mono tabular-nums whitespace-nowrap">
                          {fmt(inc.startedAt)}{inc.endedAt ? ` → ${fmt(inc.endedAt)}` : ''}
                        </p>
                        <p className="text-[11px] text-gray-600 mt-0.5">{durationLabel(inc)}</p>
                      </td>

                      <td className="px-5 py-4 text-right">
                        {response ? (
                          <span className="inline-block text-xs font-mono tabular-nums px-2 py-1 rounded bg-gray-800 text-gray-300">
                            {response}
                          </span>
                        ) : <span className="text-sm text-gray-600">—</span>}
                      </td>

                      <td className="px-5 py-4">
                        {inc.outcome ? (
                          <span className="inline-block text-[11px] px-2 py-1 rounded-full bg-green-600/15 text-green-400">
                            {inc.outcome}
                          </span>
                        ) : (
                          <span className="inline-block text-[11px] px-2 py-1 rounded-full bg-gray-800 text-gray-500">
                            Not recorded
                          </span>
                        )}
                      </td>

                      {/* The note had a Delete button pressed against it. Incident records
                          are the audit trail for who was paged, so that is gone entirely. */}
                      <td className="px-5 py-4">
                        <DebriefNoteCell
                          eventId={inc.id}
                          saved={debriefNotes[inc.id] || ''}
                          onSave={saveDebriefNote}
                        />
                      </td>
                    </tr>
                  )
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Activate Code Modal */}
      {showActivateModal && (
        <ActivateCodeModal
          trees={phoneTrees}
          locations={locations.map(l => l.name)}
          onActivate={handleActivate}
          onCreateCodeType={handleCreateCodeType}
          onClose={() => setShowActivateModal(false)}
        />
      )}
    </div>
  )
}

// ─── DEBRIEF NOTE ────────────────────────────────────────────────────────
//
// The note used to save itself 800ms after the last keystroke, and failures were
// swallowed. That made an incident's written record depend on a timer nobody could see,
// and let a half-typed sentence become the saved version.
//
// Saving is now deliberate, and a note cannot be emptied: a debrief is part of the
// incident's record, so Save stays disabled on blank text. Existing text can still be
// corrected and extended.

function DebriefNoteCell({ eventId, saved, onSave }: {
  eventId: number
  saved: string
  onSave: (eventId: number, notes: string) => Promise<void>
}) {
  const [draft, setDraft] = useState(saved)
  const [state, setState] = useState<'idle' | 'saving' | 'saved' | 'error'>('idle')

  // Re-sync if the row reloads with a newer note than the one being edited.
  useEffect(() => { setDraft(saved); setState('idle') }, [saved])

  const trimmed = draft.trim()
  const dirty = trimmed !== saved.trim()
  const canSave = dirty && trimmed.length > 0

  async function handleSave() {
    if (!canSave) return
    setState('saving')
    try {
      await onSave(eventId, trimmed)
      setState('saved')
    } catch {
      setState('error')
    }
  }

  return (
    <div className="min-w-[15rem]">
      <div className="flex items-start gap-1.5">
        <textarea
          rows={2}
          value={draft}
          onChange={e => { setDraft(e.target.value); setState('idle') }}
          placeholder="What happened, and what changed?"
          className="flex-1 bg-gray-800/70 border border-gray-700 rounded-lg px-3 py-1.5 text-xs text-gray-200 placeholder:text-gray-600 focus:outline-none focus:border-amber-600 resize-y"
        />
        <button
          type="button"
          onClick={handleSave}
          disabled={!canSave || state === 'saving'}
          className="shrink-0 px-2.5 py-1.5 rounded-lg text-xs bg-amber-600 hover:bg-amber-700 text-white disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
        >
          {state === 'saving' ? '…' : 'Save'}
        </button>
      </div>
      {state === 'saved' && <p className="text-[10px] text-green-500 mt-1">Saved</p>}
      {state === 'error' && <p className="text-[10px] text-red-400 mt-1">Could not save. Try again.</p>}
      {state === 'idle' && dirty && trimmed.length === 0 && saved.trim().length > 0 && (
        <p className="text-[10px] text-gray-500 mt-1">A debrief note cannot be emptied.</p>
      )}
    </div>
  )
}

// ─── ADD A CODE TYPE ─────────────────────────────────────────────────────
//
// A code is a phone tree, so defining one is creating the tree. The department is
// required rather than optional: a tree with none belongs to no tenant, which makes it
// invisible to the person who just created it and silences its live updates.

function AddCodeTypeForm({ onCreate }: {
  onCreate: (name: string, treeType: string, departmentId: number) => Promise<void>
}) {
  const [name, setName] = useState('')
  const [departmentId, setDepartmentId] = useState<number | ''>('')
  const [departments, setDepartments] = useState<Department[]>([])
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    departmentsApi.getAll()
      .then(d => { if (!cancelled) setDepartments(d) })
      .catch(() => { /* the required check below explains an empty list */ })
    return () => { cancelled = true }
  }, [])

  // "Rapid Response Team" -> "rapid-response-team", trimmed to the column's 20 chars.
  const treeType = name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '').slice(0, 20)

  async function submit() {
    if (!name.trim()) { setError('Give the code a name.'); return }
    if (!treeType) { setError('The name needs at least one letter or number.'); return }
    if (departmentId === '') {
      setError(departments.length === 0
        ? 'No departments are available to you, so a code cannot be added yet.'
        : 'Choose the department that owns this code.')
      return
    }
    setSaving(true); setError(null)
    try {
      await onCreate(name.trim(), treeType, Number(departmentId))
    } catch {
      setError('Could not add the code. A code with that name may already exist here.')
    } finally { setSaving(false) }
  }

  return (
    <div className="mt-3 border border-gray-800 rounded-lg p-4 space-y-3 bg-gray-800/40">
      {error && (
        <div className="flex items-center gap-2 text-xs text-red-400">
          <AlertTriangle className="w-3.5 h-3.5 shrink-0" />{error}
        </div>
      )}
      <div>
        <label className="block text-xs text-gray-500 mb-1">Code name *</label>
        <input type="text" value={name} onChange={e => setName(e.target.value)}
          placeholder="e.g. Rapid Response, Code Orange, Security Assist"
          className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600" />
        {treeType && <p className="text-[10px] text-gray-600 mt-1 font-mono">id: {treeType}</p>}
      </div>
      <div>
        <label className="block text-xs text-gray-500 mb-1">Department *</label>
        <select value={departmentId}
          onChange={e => setDepartmentId(e.target.value === '' ? '' : Number(e.target.value))}
          className="w-full bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600">
          <option value="">Select a department</option>
          {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
        </select>
      </div>
      <button type="button" onClick={submit} disabled={saving}
        className="w-full px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium disabled:opacity-50">
        {saving ? 'Adding…' : 'Add code type'}
      </button>
      <p className="text-[10px] text-gray-600">
        This creates the code with nobody in it. Add who to call under Phone Trees before
        relying on it.
      </p>
    </div>
  )
}

// ─── ACTIVATE CODE MODAL ─────────────────────────────────────────────────

function ActivateCodeModal({
  trees, locations, onActivate, onCreateCodeType, onClose,
}: {
  trees: PhoneTree[]
  locations: string[]
  onActivate: (codeType: string, location: string, notes: string, requestedByName?: string) => Promise<void>
  onCreateCodeType: (name: string, treeType: string, departmentId: number) => Promise<PhoneTree>
  onClose: () => void
}) {
  const [selectedCode, setSelectedCode] = useState(trees[0]?.treeType ?? '')
  const [addingCode, setAddingCode] = useState(false)
  const [location, setLocation] = useState(locations[0])
  const [notes, setNotes] = useState('')
  const [requestedByName, setRequestedByName] = useState('')
  const selectedTree = trees.find(t => t.treeType === selectedCode)
  const [activating, setActivating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const tree = trees.find(t => t.treeType === selectedCode)
    if (!tree) { setError('Choose a code to raise, or add one below.'); return }
    setActivating(true)
    setError(null)
    try {
      await onActivate(selectedCode, location, notes, requestedByName)
      onClose()
    } catch { setError('Failed to activate.') }
    finally { setActivating(false) }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4 max-h-[90vh] overflow-y-auto" onClick={e => e.stopPropagation()}>
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <div>
            <h2 className="text-lg font-medium">Activate Emergency Code</h2>
            <p className="text-xs text-gray-500 mt-0.5">This will trigger InformaCast broadcast → SIP trunk overhead paging → mobile push</p>
          </div>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg"><X className="w-5 h-5" /></button>
        </div>
        <form onSubmit={handleSubmit} className="p-5 space-y-4">
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 shrink-0" />{error}
            </div>
          )}

          <div>
            <div className="flex items-center justify-between mb-2">
              <label className="block text-xs text-gray-500 uppercase tracking-wider">Code Type</label>
              <button type="button" onClick={() => setAddingCode(v => !v)}
                className="text-xs text-amber-500 hover:text-amber-400">
                {addingCode ? 'Cancel' : '+ Add a code type'}
              </button>
            </div>

            {trees.length === 0 && !addingCode && (
              <p className="text-xs text-gray-500 bg-gray-800/60 border border-gray-800 rounded-lg px-4 py-3">
                No codes are defined for this subscription yet. Add one to raise it here —
                you choose the name, so it can match how your site actually operates.
              </p>
            )}

            <div className="grid grid-cols-2 gap-2">
              {trees.map(t => {
                const responders = t.nodes?.length ?? 0
                return (
                  <button
                    key={t.id}
                    type="button"
                    onClick={() => setSelectedCode(t.treeType)}
                    className={`text-left border rounded-lg p-3 transition-colors ${
                      selectedCode === t.treeType
                        ? 'bg-red-600/10 border-red-600/50'
                        : 'bg-gray-800 border-gray-700 hover:border-gray-600'
                    }`}
                  >
                    <p className="text-sm font-medium">{t.name}</p>
                    <p className="text-[10px] text-gray-500 mt-0.5">
                      {CODE_TYPE_HINTS[t.treeType]
                        ?? (responders > 0 ? `${responders} to call` : 'No one to call yet')}
                    </p>
                  </button>
                )
              })}
            </div>

            {addingCode && (
              <AddCodeTypeForm
                onCreate={async (name, treeType, departmentId) => {
                  const created = await onCreateCodeType(name, treeType, departmentId)
                  setSelectedCode(created.treeType)
                  setAddingCode(false)
                }}
              />
            )}
          </div>

          {/*
            A code whose tree has nobody in it dispatches to nobody. Raising it is still
            allowed -- during an emergency the operator decides, not this dialog -- but it
            must never look like a working page.
          */}
          {selectedTree && (selectedTree.nodes?.length ?? 0) === 0 && (
            <div className="flex items-start gap-2 text-sm text-red-300 bg-red-600/10 border border-red-600/40 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
              <span>
                <strong>{selectedTree.name} has no one to call.</strong> Raising it will
                record the incident and page nobody. Add responders under Phone Trees, and
                escalate by phone in the meantime.
              </span>
            </div>
          )}

          <div>
            <label className="block text-xs text-gray-500 uppercase tracking-wider mb-2">Location</label>
            <select value={location} onChange={e => setLocation(e.target.value)}
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2.5 text-sm focus:outline-none focus:border-amber-600">
              {locations.map(l => <option key={l} value={l}>{l}</option>)}
            </select>
          </div>

          <div>
            <label className="block text-xs text-gray-500 uppercase tracking-wider mb-2">Called in by (reporter, optional)</label>
            <input type="text" value={requestedByName} onChange={e => setRequestedByName(e.target.value)}
              placeholder="e.g. RN Smith, Emergency Dept"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2.5 text-sm focus:outline-none focus:border-amber-600" />
          </div>

          <div>
            <label className="block text-xs text-gray-500 uppercase tracking-wider mb-2">Notes (optional)</label>
            <input type="text" value={notes} onChange={e => setNotes(e.target.value)}
              placeholder="e.g. witnessed collapse, pulse present"
              className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2.5 text-sm focus:outline-none focus:border-amber-600" />
          </div>

          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors">Cancel</button>
            <button type="submit" disabled={activating}
              className="flex items-center gap-2 px-5 py-2 bg-red-600 hover:bg-red-700 rounded-lg text-sm font-bold transition-colors disabled:opacity-50">
              {activating ? <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" /> : <Play className="w-4 h-4" />}
              {activating ? 'Dispatching...' : 'Dispatch Now'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
