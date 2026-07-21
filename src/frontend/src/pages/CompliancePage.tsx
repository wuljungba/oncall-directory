import { useState, useEffect, useMemo } from 'react'
import { AlertTriangle, CheckCircle, Clock, ShieldAlert, Download } from 'lucide-react'
import { complianceApi } from '@/services/api'
import type { DutyHourViolation } from '@/types'

export default function CompliancePage() {
  const [violations, setViolations] = useState<DutyHourViolation[]>([])
  const [loading, setLoading] = useState(true)
  const [weeksBack, setWeeksBack] = useState(4)

  useEffect(() => {
    loadViolations()
  }, [weeksBack])

  async function loadViolations() {
    try {
      setLoading(true)
      const from = new Date(Date.now() - weeksBack * 7 * 24 * 60 * 60 * 1000).toISOString()
      const to = new Date().toISOString()
      const data = await complianceApi.checkAll(from, to)
      setViolations(data)
    } catch { /* ignore */ }
    finally { setLoading(false) }
  }

  const stats = useMemo(() => ({
    total: violations.length,
    breaches: violations.filter(v => v.severity >= 2).length,
    warnings: violations.filter(v => v.severity === 1).length,
  }), [violations])

  // Weekly trend data
  const weeklyTrend = useMemo(() => {
    const weeks: { label: string; count: number; breaches: number }[] = []
    for (let w = weeksBack - 1; w >= 0; w--) {
      const weekStart = new Date(Date.now() - w * 7 * 24 * 60 * 60 * 1000)
      const weekEnd = new Date(weekStart.getTime() + 7 * 24 * 60 * 60 * 1000)
      const weekVios = violations.filter(v => {
        const d = new Date(v.violatedAt)
        return d >= weekStart && d < weekEnd
      })
      weeks.push({
        label: weekStart.toLocaleDateString('en-US', { month: 'short', day: 'numeric' }),
        count: weekVios.length,
        breaches: weekVios.filter(v => v.severity >= 2).length,
      })
    }
    return weeks
  }, [violations, weeksBack])

  const maxTrend = Math.max(...weeklyTrend.map(w => w.count), 1)

  function handleExportCsv() {
    const header = 'Employee,Description,Severity,Date,Rule\n'
    const rows = violations.map(v =>
      `"${v.employee?.firstName || ''} ${v.employee?.lastName || ''}","${v.description}",${v.severity >= 2 ? 'Breach' : 'Warning'},"${new Date(v.violatedAt).toLocaleDateString()}",${v.rule?.name || ''}`
    ).join('\n')
    const blob = new Blob([header + rows], { type: 'text/csv' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `compliance-report-${new Date().toISOString().slice(0, 10)}.csv`
    a.click(); URL.revokeObjectURL(url)
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Duty-Hour Compliance</h1>
        <div className="flex items-center gap-2">
          <select value={weeksBack} onChange={e => setWeeksBack(Number(e.target.value))}
            className="bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm">
            <option value={2}>Last 2 weeks</option>
            <option value={4}>Last 4 weeks</option>
            <option value={12}>Last 12 weeks</option>
          </select>
          <button onClick={handleExportCsv} disabled={violations.length === 0}
            className="flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-700 disabled:opacity-50 rounded-lg text-sm">
            <Download className="w-4 h-4" /> Export CSV
          </button>
        </div>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Total Violations</p>
              <p className={`text-3xl font-bold mt-1 ${stats.total > 0 ? 'text-red-500' : 'text-green-500'}`}>{stats.total}</p>
            </div>
            <ShieldAlert className="w-10 h-10 text-red-600/30" />
          </div>
        </div>
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Breaches</p>
              <p className={`text-3xl font-bold mt-1 ${stats.breaches > 0 ? 'text-red-500' : 'text-green-500'}`}>{stats.breaches}</p>
            </div>
            <AlertTriangle className="w-10 h-10 text-red-600/30" />
          </div>
        </div>
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Warnings</p>
              <p className={`text-3xl font-bold mt-1 ${stats.warnings > 0 ? 'text-yellow-500' : 'text-green-500'}`}>{stats.warnings}</p>
            </div>
            <Clock className="w-10 h-10 text-yellow-600/30" />
          </div>
        </div>
      </div>

      {/* Weekly trend bar chart */}
      {weeklyTrend.length > 0 && (
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <h2 className="font-medium mb-4">Weekly Trend</h2>
          <div className="flex items-end gap-3 h-32">
            {weeklyTrend.map((w, i) => (
              <div key={i} className="flex-1 flex flex-col items-center gap-1">
                <div className="w-full flex flex-col-reverse" style={{ height: '100px' }}>
                  <div
                    className="w-full bg-red-600/30 rounded-t transition-all"
                    style={{ height: `${(w.breaches / maxTrend) * 100}%` }}
                    title={`${w.breaches} breaches`}
                  />
                  <div
                    className="w-full bg-yellow-600/30 rounded-t transition-all"
                    style={{ height: `${((w.count - w.breaches) / maxTrend) * 100}%` }}
                    title={`${w.count - w.breaches} warnings`}
                  />
                </div>
                <span className="text-[10px] text-gray-500 -rotate-45 origin-left">{w.label}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Violations List */}
      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800 flex items-center justify-between">
          <h2 className="font-medium">Violations</h2>
          <span className="text-xs text-gray-500">{violations.length} found</span>
        </div>
        <div className="p-5">
          {loading ? (
            <div className="flex justify-center py-12"><div className="animate-spin h-6 w-6 border-b-2 border-amber-600 rounded-full" /></div>
          ) : violations.length === 0 ? (
            <div className="flex flex-col items-center py-12 text-gray-500">
              <CheckCircle className="w-12 h-12 mb-4 text-green-600/50" />
              <p className="text-sm">All clear — no duty-hour violations</p>
            </div>
          ) : (
            <div className="space-y-2 max-h-[500px] overflow-y-auto">
              {violations.map(v => (
                <div key={v.id} className={`p-3 rounded-lg border ${v.severity >= 2 ? 'bg-red-600/10 border-red-600/30' : 'bg-yellow-600/10 border-yellow-600/30'}`}>
                  <div className="flex items-start justify-between gap-3">
                    <div className="flex items-start gap-3 min-w-0">
                      {v.severity >= 2 ? <AlertTriangle className="w-4 h-4 text-red-500 mt-0.5 flex-shrink-0" /> : <Clock className="w-4 h-4 text-yellow-500 mt-0.5 flex-shrink-0" />}
                      <div className="min-w-0">
                        <p className="text-sm font-medium truncate">{v.employee?.firstName} {v.employee?.lastName}</p>
                        <p className="text-xs text-gray-400 mt-0.5">{v.description}</p>
                        <p className="text-xs text-gray-600 mt-0.5">{new Date(v.violatedAt).toLocaleDateString()}{v.rule ? ` · ${v.rule.name}` : ''}</p>
                      </div>
                    </div>
                    <span className={`text-xs px-2 py-0.5 rounded-full flex-shrink-0 ${v.severity >= 2 ? 'bg-red-600/20 text-red-500' : 'bg-yellow-600/20 text-yellow-500'}`}>
                      {v.severity >= 2 ? 'Breach' : 'Warning'}
                    </span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
