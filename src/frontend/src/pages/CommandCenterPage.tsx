import { useState, useEffect, useRef, useCallback } from 'react'
import { AlertTriangle, CheckCircle, Play, X, PhoneCall } from 'lucide-react'
import { commandCenterApi, phoneTreesApi, codeCallLocationsApi } from '@/services/api'
import { useSignalR } from '@/hooks/useSignalR'
import type { PhoneTreeEvent, PhoneTree, CodeCallLocation } from '@/types'

const CODE_TYPES = [
  { key: 'code-blue', label: 'Medical Emergency', sub: 'Cardiac/respiratory arrest' },
  { key: 'code-red', label: 'Fire', sub: 'Smoke / fire detected' },
  { key: 'code-green', label: 'Evacuation', sub: 'Facility evacuation' },
  { key: 'code-silver', label: 'Active Threat', sub: 'Armed person / lockdown' },
  { key: 'code-grey', label: 'Severe Weather', sub: 'Weather emergency' },
  { key: 'code-pink', label: 'Infant Abduction', sub: 'Nursery / peds security' },
]

const PIPELINE_STEPS = [
  { key: 'created', label: 'Event Created' },
  { key: 'cucm_check', label: 'CUCM Device Check' },
  { key: 'informacast', label: 'InformaCast Broadcast' },
  { key: 'vocera', label: 'Vocera Alert' },
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
  const [deletingId, setDeletingId] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [showArchived, setShowArchived] = useState(false)
  const [debriefNotes, setDebriefNotes] = useState<Record<number, string>>({})
  const debriefTimersRef = useRef<Record<number, ReturnType<typeof setTimeout>>>({})
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
        const p = lastEvent.payload as any
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

  async function ensurePhoneTree(codeType: string): Promise<PhoneTree | null> {
    let tree = phoneTrees.find(t => t.treeType === codeType)
    if (!tree) {
      const codeDef = CODE_TYPES.find(c => c.key === codeType)
      try {
        tree = await phoneTreesApi.create({
          name: codeDef?.label || codeType,
          treeType: codeType as any,
          procedure: `Standard response procedure for ${codeType}. Customize in Code Call Configuration.`,
        })
        setPhoneTrees(prev => [...prev, tree!])
        addLogEntry('info', `Auto-created phone tree for ${codeDef?.label || codeType}`)
      } catch (err) {
        addLogEntry('info', `Failed to create phone tree: ${err}`)
        return null
      }
    }
    return tree
  }

  async function handleActivate(codeType: string, location: string, notes: string, requestedByName = '') {
    const tree = await ensurePhoneTree(codeType)
    if (!tree) {
      addLogEntry('info', 'Could not activate: no code call configured and auto-creation failed.')
      return
    }
    try {
      const evt = await phoneTreesApi.createEvent(tree.id, {
        startedAt: new Date().toISOString(),
        location,
        notes: notes || undefined,
        requestedByName: requestedByName.trim() || undefined,
        confirm: true,
      } as any)
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
    } catch { /* ignore */ }
    setCompleting(null)
  }

  async function handleDeleteEvent(eventId: number) {
    if (!window.confirm('Permanently delete this incident from history?')) return
    setDeletingId(eventId)
    try {
      await phoneTreesApi.deleteEvent(eventId)
      addLogEntry('info', `Incident #${eventId} deleted from history`)
      await loadData()
    } catch { /* ignore */ }
    setDeletingId(null)
  }

  const filteredResolved = resolvedIncidents.filter(inc => matchesSearch(inc, search) && (showArchived || !isArchived(inc)))

  const saveDebriefNote = useCallback(async (eventId: number, notes: string) => {
    try {
      await commandCenterApi.saveDebriefNotes(eventId, notes)
    } catch { /* ignore */ }
  }, [])

  function fmt(s?: string) {
  return s ? new Date(s).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '—'
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

function getStepStatus(evt: PhoneTreeEvent, stepKey: string) {
    if (evt.status === 'completed') return 'done'
    const step = evt.dispatchSteps?.find(s => s.stepKey === stepKey)
    if (!step) return stepKey === 'created' ? 'done' : 'pending'
    if (step.status === 'completed') return 'done'
    if (step.status === 'failed') return 'failed'
    return 'active'
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
                      return (
                        <span key={step.key} className={`inline-flex items-center gap-1 font-mono text-[10px] px-2 py-1 rounded-md border ${
                          status === 'done' ? 'bg-green-600/20 text-green-500 border-green-600/30' :
                          status === 'failed' ? 'bg-red-600/20 text-red-500 border-red-600/30' :
                          status === 'active' ? 'bg-yellow-600/20 text-yellow-500 border-yellow-600/30 animate-pulse' :
                          'bg-gray-800 text-gray-600 border-gray-700'
                        }`}>
                          <span className="w-1.5 h-1.5 rounded-full bg-current" />
                          {step.label}
                        </span>
                      )
                    })}
                  </div>
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
                    <button
                      onClick={() => handleDeleteEvent(inc.id)}
                      disabled={deletingId === inc.id}
                      className="text-xs px-3 py-1.5 bg-red-600/10 hover:bg-red-600/20 text-red-400 rounded-lg transition-colors disabled:opacity-50"
                    >
                      Delete
                    </button>
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
            {logEntries.length === 0 ? (
              <div className="text-gray-600 py-8 text-center">Awaiting activation. All paging channels nominal.</div>
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
              <tr className="text-left text-xs text-gray-500 uppercase tracking-wider">
                <th className="px-5 py-3 font-medium">Incident</th>
                <th className="px-5 py-3 font-medium">Location</th>
                <th className="px-5 py-3 font-medium">Code</th>
                <th className="px-5 py-3 font-medium">Reported / Triggered</th>
                <th className="px-5 py-3 font-medium">Time</th>
                <th className="px-5 py-3 font-medium">Response Time</th>
                <th className="px-5 py-3 font-medium">Outcome</th>
                <th className="px-5 py-3 font-medium">Notes</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-800">
              {filteredResolved.length === 0 ? (
                <tr><td colSpan={8} className="text-center text-gray-600 py-10 text-sm">
                  {resolvedIncidents.length === 0 ? 'No incidents yet.' : 'No incidents match. Try clearing search or showing archived.'}
                </td></tr>
              ) : (
                filteredResolved.map(inc => (
                  <tr key={inc.id}>
                    <td className="px-5 py-3 text-gray-300">#{inc.id} — {inc.phoneTree?.name || 'Code Call'}</td>
                    <td className="px-5 py-3 text-gray-500">{inc.location || '—'}</td>
                    <td className="px-5 py-3 text-gray-500">{inc.phoneTree?.treeType || '—'}</td>
                    <td className="px-5 py-3 text-gray-400">
                      {inc.requestedByName ? <span>Reported by <span className="text-gray-200">{inc.requestedByName}</span></span> : null}
                      {inc.requestedByName && trigName(inc) ? <span className="text-gray-600"> · </span> : null}
                      {trigName(inc) ? <span>Triggered by <span className="text-gray-200">{trigName(inc)}</span></span> : null}
                      {inc.notifiedByName ? <span className="text-gray-600"> · </span> : null}
                      {inc.notifiedByName ? <span>Notified: <span className="text-gray-200">{inc.notifiedByName}</span></span> : null}
                      {!inc.requestedByName && !trigName(inc) && !inc.notifiedByName ? '—' : ''}
                    </td>
                    <td className="px-5 py-3 text-gray-300 font-mono">{fmt(inc.startedAt)}{inc.endedAt ? ` – ${fmt(inc.endedAt)}` : ''}</td>
                    <td className="px-5 py-3 text-gray-300 font-mono">
                      {inc.responseTimeSeconds ? `${Math.floor(inc.responseTimeSeconds / 60)}m ${inc.responseTimeSeconds % 60}s` : '—'}
                    </td>
                    <td className="px-5 py-3 text-gray-500">{inc.outcome || '—'}</td>
                    <td className="px-5 py-3">
                      <div className="flex items-center gap-1">
                        <input
                          type="text"
                          placeholder="Add debrief note..."
                          value={debriefNotes[inc.id] || ''}
                          onChange={e => {
                            const val = e.target.value
                            setDebriefNotes(prev => ({ ...prev, [inc.id]: val }))
                            if (debriefTimersRef.current[inc.id]) clearTimeout(debriefTimersRef.current[inc.id])
                            debriefTimersRef.current[inc.id] = setTimeout(() => {
                              saveDebriefNote(inc.id, val)
                            }, 800)
                          }}
                          className="flex-1 min-w-0 bg-gray-800 border border-gray-700 rounded px-2 py-1 text-xs focus:outline-none focus:border-amber-600"
                        />
                        <button
                          onClick={() => handleDeleteEvent(inc.id)}
                          disabled={deletingId === inc.id}
                          title="Delete from history"
                          className="shrink-0 px-2 py-1 bg-red-600/10 hover:bg-red-600/20 text-red-400 rounded text-xs transition-colors disabled:opacity-50"
                        >
                          {deletingId === inc.id ? '…' : 'Delete'}
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
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
          codeTypes={CODE_TYPES}
          onActivate={handleActivate}
          onClose={() => setShowActivateModal(false)}
        />
      )}
    </div>
  )
}

// ─── ACTIVATE CODE MODAL ─────────────────────────────────────────────────

function ActivateCodeModal({
  trees, locations, codeTypes, onActivate, onClose,
}: {
  trees: PhoneTree[]
  locations: string[]
  codeTypes: typeof CODE_TYPES
  onActivate: (codeType: string, location: string, notes: string, requestedByName?: string) => Promise<void>
  onClose: () => void
}) {
  const [selectedCode, setSelectedCode] = useState(codeTypes[0].key)
  const [location, setLocation] = useState(locations[0])
  const [notes, setNotes] = useState('')
  const [requestedByName, setRequestedByName] = useState('')
  const [activating, setActivating] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const tree = trees.find(t => t.treeType === selectedCode)
    if (!tree) { setError('No code call configured for this type. Please create one in Code Call Configuration.'); return }
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
      <div className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4" onClick={e => e.stopPropagation()}>
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
            <label className="block text-xs text-gray-500 uppercase tracking-wider mb-2">Code Type</label>
            <div className="grid grid-cols-2 gap-2">
              {codeTypes.map(ct => (
                <button
                  key={ct.key}
                  type="button"
                  onClick={() => setSelectedCode(ct.key)}
                  className={`text-left border rounded-lg p-3 transition-colors ${
                    selectedCode === ct.key
                      ? 'bg-red-600/10 border-red-600/50'
                      : 'bg-gray-800 border-gray-700 hover:border-gray-600'
                  }`}
                >
                  <p className="text-sm font-medium">{ct.label}</p>
                  <p className="text-[10px] text-gray-500 mt-0.5">{ct.sub}</p>
                </button>
              ))}
            </div>
          </div>

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
