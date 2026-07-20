import { useState, useEffect } from 'react'
import { AlertTriangle, CheckCircle, Clock, ShieldAlert } from 'lucide-react'
import { complianceApi } from '@/services/api'
import type { DutyHourViolation } from '@/types'

export default function CompliancePage() {
  const [violations, setViolations] = useState<DutyHourViolation[]>([])
  const [loading, setLoading] = useState(true)
  const [stats, setStats] = useState({ total: 0, breaches: 0, warnings: 0 })

  useEffect(() => {
    loadViolations()
  }, [])

  async function loadViolations() {
    try {
      setLoading(true)
      const data = await complianceApi.checkAll()
      setViolations(data)
      setStats({
        total: data.length,
        breaches: data.filter(v => v.severity >= 2).length,
        warnings: data.filter(v => v.severity === 1).length,
      })
    } catch (err) {
      console.error('Failed to load compliance data:', err)
    } finally {
      setLoading(false)
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
      <h1 className="text-2xl font-bold">Duty-Hour Compliance</h1>

      {/* Stats Cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Total Violations</p>
              <p className={`text-3xl font-bold mt-1 ${stats.total > 0 ? 'text-red-500' : 'text-green-500'}`}>
                {stats.total}
              </p>
            </div>
            <ShieldAlert className="w-10 h-10 text-red-600/30" />
          </div>
        </div>
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Breaches</p>
              <p className={`text-3xl font-bold mt-1 ${stats.breaches > 0 ? 'text-red-500' : 'text-green-500'}`}>
                {stats.breaches}
              </p>
            </div>
            <AlertTriangle className="w-10 h-10 text-red-600/30" />
          </div>
        </div>
        <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-gray-500">Warnings</p>
              <p className={`text-3xl font-bold mt-1 ${stats.warnings > 0 ? 'text-yellow-500' : 'text-green-500'}`}>
                {stats.warnings}
              </p>
            </div>
            <Clock className="w-10 h-10 text-yellow-600/30" />
          </div>
        </div>
      </div>

      {/* Violations List */}
      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800">
          <h2 className="font-medium">Recent Violation Checks</h2>
        </div>
        <div className="p-5">
          {violations.length === 0 ? (
            <div className="flex flex-col items-center justify-center py-12 text-gray-500">
              <CheckCircle className="w-12 h-12 mb-4 text-green-600/50" />
              <p className="text-sm">No duty-hour violations</p>
              <p className="text-xs mt-1">All employees are within compliance limits</p>
            </div>
          ) : (
            <div className="space-y-3">
              {violations.map((v) => (
                <div
                  key={v.id}
                  className={`p-4 rounded-lg border ${
                    v.severity >= 2
                      ? 'bg-red-600/10 border-red-600/30'
                      : 'bg-yellow-600/10 border-yellow-600/30'
                  }`}
                >
                  <div className="flex items-start justify-between">
                    <div className="flex items-start gap-3">
                      {v.severity >= 2 ? (
                        <AlertTriangle className="w-5 h-5 text-red-500 flex-shrink-0 mt-0.5" />
                      ) : (
                        <Clock className="w-5 h-5 text-yellow-500 flex-shrink-0 mt-0.5" />
                      )}
                      <div>
                        <p className="text-sm font-medium">
                          {v.employee?.firstName} {v.employee?.lastName}
                        </p>
                        <p className="text-xs text-gray-400 mt-0.5">{v.description}</p>
                        <p className="text-xs text-gray-600 mt-1">
                          {new Date(v.violatedAt).toLocaleDateString()}
                          {v.rule && ` — Rule: ${v.rule.name}`}
                        </p>
                      </div>
                    </div>
                    <span
                      className={`text-xs px-2 py-0.5 rounded-full ${
                        v.severity >= 2
                          ? 'bg-red-600/20 text-red-500'
                          : 'bg-yellow-600/20 text-yellow-500'
                      }`}
                    >
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
