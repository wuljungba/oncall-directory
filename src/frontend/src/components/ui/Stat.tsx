import type { ReactNode } from 'react'

type Tone = 'amber' | 'blue' | 'green' | 'red'

const TONES: Record<Tone, { value: string; icon: string }> = {
  amber: { value: 'text-amber-500', icon: 'text-amber-600/30' },
  blue: { value: 'text-blue-500', icon: 'text-blue-600/30' },
  green: { value: 'text-green-500', icon: 'text-green-600/30' },
  red: { value: 'text-red-500', icon: 'text-red-600/30' },
}

/** Metric/stat card. */
export function Stat({
  label,
  value,
  icon,
  tone = 'amber',
}: {
  label: string
  value: ReactNode
  icon?: ReactNode
  tone?: Tone
}) {
  const t = TONES[tone]
  return (
    <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
      <div className="flex items-center justify-between">
        <div>
          <p className="text-sm text-gray-500">{label}</p>
          <p className={`mt-1 text-3xl font-bold ${t.value}`}>{value}</p>
        </div>
        {icon && <div className={t.icon}>{icon}</div>}
      </div>
    </div>
  )
}