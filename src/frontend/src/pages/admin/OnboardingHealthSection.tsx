import { useCallback, useEffect, useState } from 'react'
import { AlertTriangle, CheckCircle, Users, ShieldCheck } from 'lucide-react'
import { onboardingApi } from '@/services/api'
import type { OnboardingHealth } from '@/types'

/**
 * Admin view of the onboarding standard (docs/onboarding-standard.md). Flags records
 * missing a Source classification, a sign-in identity, or the baseline permission.
 */
export default function OnboardingHealthSection() {
  const [health, setHealth] = useState<OnboardingHealth | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setHealth(await onboardingApi.getHealth())
    } catch {
      setError('Failed to load onboarding health.')
    }
    setLoading(false)
  }, [])

  useEffect(() => { load() }, [load])

  const sourceChip = (src: string) =>
    src === 'Ad' ? 'bg-blue-600/20 text-blue-500'
      : src === 'CsvImport' ? 'bg-purple-600/20 text-purple-500'
      : src === 'Local' ? 'bg-gray-700 text-gray-300'
      : 'bg-red-600/20 text-red-400'

  const sourceOrder = ['Ad', 'CsvImport', 'Local']

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h2 className="font-medium flex items-center gap-2">
          <ShieldCheck className="w-5 h-5 text-amber-500" /> Onboarding Status
        </h2>
        <button onClick={load} className="text-xs text-gray-400 hover:text-gray-200">Refresh</button>
      </div>

      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" /><span>{error}</span>
        </div>
      )}

      {loading ? (
        <div className="flex justify-center py-16"><div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" /></div>
      ) : health ? (
        <>
          {/* Summary */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
              <p className="text-sm text-gray-500">Records</p>
              <p className="text-3xl font-bold mt-1">{health.total}</p>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {sourceOrder.map(s => health.bySource[s] ? (
                  <span key={s} className={`text-xs px-2 py-0.5 rounded ${sourceChip(s)}`}>{s}: {health.bySource[s]}</span>
                ) : null)}
              </div>
            </div>
            <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
              <p className="text-sm text-gray-500">Needs attention</p>
              <p className={`text-3xl font-bold mt-1 ${health.withIssues > 0 ? 'text-red-500' : 'text-green-500'}`}>{health.withIssues}</p>
            </div>
            <div className="bg-gray-900 border border-gray-800 rounded-xl p-5 flex items-center justify-center text-gray-400">
              <Users className="w-10 h-10" />
            </div>
          </div>

          {/* Issues */}
          <div className="bg-gray-900 border border-gray-800 rounded-xl">
            <div className="px-5 py-4 border-b border-gray-800">
              <p className="text-sm text-gray-500">
                {health.withIssues === 0
                  ? 'All employees satisfy the onboarding standard.'
                  : `${health.withIssues} employee${health.withIssues !== 1 ? 's' : ''} need attention`}
              </p>
            </div>
            {health.issues.length === 0 ? (
              <div className="flex flex-col items-center py-14 text-gray-500">
                <CheckCircle className="w-12 h-12 mb-4 text-green-600/50" />
                <p className="text-sm">All good — every record has a Source, identity, and baseline permission.</p>
              </div>
            ) : (
              <div className="max-h-[500px] overflow-y-auto divide-y divide-gray-800">
                {health.issues.map(issue => (
                  <div key={issue.id} className="px-5 py-4">
                    <div className="flex items-center gap-2 flex-wrap">
                      <p className="text-sm font-medium">{issue.name}</p>
                      <span className={`text-xs px-2 py-0.5 rounded ${sourceChip(issue.source)}`}>{issue.source}</span>
                      {!issue.isActive && (
                        <span className="text-xs px-2 py-0.5 rounded bg-red-600/15 text-red-400">Inactive</span>
                      )}
                      <span className="text-xs text-gray-500 ml-auto truncate">{issue.email}</span>
                    </div>
                    <div className="mt-1.5 flex flex-wrap gap-1.5">
                      {issue.problems.map(p => (
                        <span key={p} className="text-xs px-2 py-0.5 rounded bg-yellow-600/10 text-yellow-500">
                          <AlertTriangle className="w-3 h-3 inline-block mr-1 -mt-0.5" />{p}
                        </span>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </>
      ) : null}
    </div>
  )
}