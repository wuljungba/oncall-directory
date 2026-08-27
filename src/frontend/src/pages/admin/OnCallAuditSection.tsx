import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Download, FileText } from 'lucide-react'
import { auditApi } from '@/services/api'
import { formatDateOnly } from '@/utils/date'
import type { OnCallReportRow } from '@/types'
import { downloadCsv } from '@/utils/download'

/**
 * Admin on-call audit report: who was on call, when, what tier, shift status, and any
 * code-call incidents raised during the shift (who triggered, who was notified, outcome).
 */
export default function OnCallAuditSection() {
  const [rows, setRows] = useState<OnCallReportRow[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [days, setDays] = useState(30)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const from = new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString()
      const to = new Date().toISOString()
      setRows(await auditApi.getOnCallReport(from, to))
    } catch {
      setError('Failed to load the on-call audit report.')
      setRows([])
    }
    setLoading(false)
  }, [days])

  useEffect(() => { load() }, [load])

  const incidentCount = useMemo(() => rows.reduce((n, r) => n + r.incidents.length, 0), [rows])

  function handleExportCsv() {
    const csvRows = [
      ['Date', 'Employee', 'Tier', 'Shift Start', 'Shift End', 'Status', 'Incidents'],
      ...rows.map(r => [
        formatDateOnly(r.start),
        r.employeeName,
        r.tier,
        new Date(r.start).toLocaleString(),
        new Date(r.end).toLocaleString(),
        r.status,
        r.incidents.map(i =>
          `#${i.id} ${i.location || ''} ${i.requestedByName ? `reported:${i.requestedByName}` : ''} ${i.initiatedByName ? `triggered:${i.initiatedByName}` : ''} ${i.notifiedByName ? `notified:${i.notifiedByName}` : ''} ${i.outcome || ''}`
        ).join(' | '),
      ]),
    ]
    downloadCsv(`oncall-audit-${new Date().toISOString().slice(0, 10)}.csv`, csvRows)
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between flex-wrap gap-3">
        <h2 className="font-medium flex items-center gap-2">
          <FileText className="w-5 h-5 text-amber-500" /> On-Call Audit Report
        </h2>
        <div className="flex items-center gap-2">
          <select value={days} onChange={e => setDays(Number(e.target.value))}
            className="bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm">
            <option value={7}>Last 7 days</option>
            <option value={30}>Last 30 days</option>
            <option value={90}>Last 90 days</option>
          </select>
          <button onClick={handleExportCsv} disabled={rows.length === 0}
            className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 disabled:opacity-50 rounded-lg text-sm">
            <Download className="w-4 h-4" /> Export CSV
          </button>
        </div>
      </div>

      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}

      {loading ? (
        <div className="flex justify-center py-16"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
      ) : rows.length === 0 ? (
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-10 text-center text-sm text-gray-500">
          No on-call shifts in this window.
        </div>
      ) : (
        <div className="bg-gray-900 border border-gray-800 rounded-xl">
          <div className="px-5 py-3 border-b border-gray-800 flex items-center justify-between">
            <span className="text-xs text-gray-500">{rows.length} shifts · {incidentCount} incidents</span>
          </div>
          <div className="overflow-x-auto max-h-[600px] overflow-y-auto">
            <table className="w-full text-sm">
              <thead className="sticky top-0 bg-gray-900">
                <tr className="text-left text-xs text-gray-500 uppercase tracking-wider border-b border-gray-800">
                  <th className="px-4 py-3 font-medium">Date</th>
                  <th className="px-4 py-3 font-medium">Employee</th>
                  <th className="px-4 py-3 font-medium">Tier</th>
                  <th className="px-4 py-3 font-medium">Shift</th>
                  <th className="px-4 py-3 font-medium">Status</th>
                  <th className="px-4 py-3 font-medium">Incidents / Contacts</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-800">
                {rows.map((r, i) => (
                  <tr key={i}>
                    <td className="px-4 py-3 text-gray-400">{formatDateOnly(r.start)}</td>
                    <td className="px-4 py-3 text-gray-200">{r.employeeName}</td>
                    <td className="px-4 py-3">
                      <span className={`text-xs px-2 py-0.5 rounded ${r.tier === 'primary' ? 'bg-amber-600/20 text-amber-500' : 'bg-gray-800 text-gray-400'}`}>{r.tier}</span>
                    </td>
                    <td className="px-4 py-3 text-gray-400 text-xs">
                      {new Date(r.start).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} – {new Date(r.end).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                    </td>
                    <td className="px-4 py-3 text-gray-500">{r.status}</td>
                    <td className="px-4 py-3">
                      {r.incidents.length === 0 ? (
                        <span className="text-xs text-gray-600">—</span>
                      ) : (
                        <div className="space-y-1 max-w-md">
                          {r.incidents.map(inc => (
                            <div key={inc.id} className="text-xs bg-gray-800/60 rounded px-2 py-1">
                              <span className="text-amber-500">#{inc.id}</span>
                              {inc.location ? <span className="text-gray-400"> {inc.location}</span> : null}
                              {inc.initiatedByName ? <span className="text-gray-300"> · triggered: {inc.initiatedByName}</span> : null}
                              {inc.requestedByName ? <span className="text-gray-400"> · reported: {inc.requestedByName}</span> : null}
                              {inc.notifiedByName ? <span className="text-gray-300"> · notified: {inc.notifiedByName}</span> : null}
                              {inc.outcome ? <span className="text-gray-500"> · {inc.outcome}</span> : null}
                            </div>
                          ))}
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  )
}