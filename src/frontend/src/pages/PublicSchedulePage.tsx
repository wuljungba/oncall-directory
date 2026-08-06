import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { publicApi } from '@/services/api'
import type { PublicCoverage, PublicCoveredUnit } from '@/types'

const TIER_LABELS: Record<string, string> = {
  primary: 'Primary',
  secondary: 'Secondary',
  tertiary: 'Tertiary',
}

const TIER_ORDER = ['primary', 'secondary', 'tertiary']

/**
 * Public, unauthenticated on-call coverage view served from a revocable permalink
 * token. Deliberately coverage-only — the backend returns no names, phones, or
 * emails, so this leaks no PHI.
 */
export default function PublicSchedulePage() {
  const { token } = useParams()
  const [data, setData] = useState<PublicCoverage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    if (!token) return
    let active = true
    publicApi
      .getOnCallCoverage(token)
      .then((res) => active && setData(res))
      .catch((e: Error) => active && setError(e?.message || 'Failed to load on-call coverage.'))
      .finally(() => active && setLoading(false))
    return () => {
      active = false
    }
  }, [token])

  return (
    <div className="min-h-screen bg-gray-950 text-gray-100">
      <header className="flex items-center justify-between border-b border-gray-800 bg-gray-900/60 px-6 py-4 backdrop-blur">
        <div className="flex items-center gap-2">
          <span className="inline-block h-2.5 w-2.5 rounded-full bg-amber-500" />
          <span className="font-semibold tracking-tight text-gray-100">On-Call Coverage</span>
        </div>
        <span className="text-xs text-gray-400">Public view · coverage only</span>
      </header>

      <main className="mx-auto max-w-6xl px-6 py-8">
        {loading && <p className="text-gray-400">Loading on-call coverage…</p>}

        {error && !loading && (
          <div className="rounded-xl border border-gray-800 bg-gray-900 p-6">
            <p className="text-lg font-medium text-red-400">Link unavailable</p>
            <p className="mt-1 text-sm text-gray-400">{error}</p>
          </div>
        )}

        {data && (
          <div>
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h1 className="text-2xl font-semibold tracking-tight">{data.tenant}</h1>
                <p className="mt-1 text-sm text-gray-400">
                  On-call coverage as of {new Date(data.coverageAt).toLocaleString()}
                </p>
              </div>
            </div>

            <div className="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {data.units.map((unit) => (
                <UnitCard key={unit.departmentId} unit={unit} />
              ))}
              {data.units.length === 0 && (
                <p className="col-span-full text-sm text-gray-400">
                  No on-call assignments are currently scheduled.
                </p>
              )}
            </div>
          </div>
        )}
      </main>
    </div>
  )
}

function UnitCard({ unit }: { unit: PublicCoveredUnit }) {
  return (
    <div className="rounded-xl border border-gray-800 bg-gray-900 p-5">
      <h2 className="font-medium text-gray-100">{unit.department}</h2>
      <div className="mt-3 space-y-2">
        {TIER_ORDER.map((tier) => {
          const t = unit.tiers[tier]
          if (!t) return null
          return (
            <div
              key={tier}
              className="flex items-center justify-between rounded-lg bg-gray-800/50 px-3 py-2"
            >
              <span className="text-sm text-gray-300">{TIER_LABELS[tier] ?? tier}</span>
              {t.covered ? (
                <span className="flex items-center gap-1.5 text-xs font-medium text-emerald-300">
                  <span className="inline-block h-1.5 w-1.5 rounded-full bg-emerald-400" />
                  Covered
                </span>
              ) : (
                <span className="flex items-center gap-1.5 text-xs font-medium text-amber-300">
                  <span className="inline-block h-1.5 w-1.5 rounded-full bg-amber-400" />
                  Not covered
                </span>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}