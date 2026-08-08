
export type PresenceTone = 'green' | 'red' | 'gray'

/** Human label for a stored presence value. Unknown/absent → "Offline". */
export function presenceLabel(p?: string): string {
  switch (p) {
    case 'available': return 'Available'
    case 'busy': return 'Busy'
    case 'dnd': return 'Do Not Disturb'
    case 'offline': return 'Offline'
    default: return 'Offline'
  }
}

/** Tailwind color for a presence dot: green available, red busy/dnd, gray otherwise. */
export function presenceTone(p?: string): PresenceTone {
  switch (p) {
    case 'available': return 'green'
    case 'busy':
    case 'dnd': return 'red'
    default: return 'gray'
  }
}

/** Dot class for the given tone. */
export function presenceDotClass(p?: string): string {
  switch (presenceTone(p)) {
    case 'green': return 'bg-green-500'
    case 'red': return 'bg-red-500'
    default: return 'bg-gray-500'
  }
}