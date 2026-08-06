import type { ReactNode } from 'react'

type Tone = 'amber' | 'blue' | 'green' | 'red' | 'gray'

const TONES: Record<Tone, string> = {
  amber: 'bg-amber-600/20 text-amber-500',
  blue: 'bg-blue-600/20 text-blue-500',
  green: 'bg-green-600/20 text-green-500',
  red: 'bg-red-600/20 text-red-500',
  gray: 'bg-gray-600/20 text-gray-400',
}

/** Status/tier pill. */
export function Badge({ children, tone = 'gray' }: { children: ReactNode; tone?: Tone }) {
  return <span className={`text-xs px-2 py-0.5 rounded-full ${TONES[tone]}`}>{children}</span>
}