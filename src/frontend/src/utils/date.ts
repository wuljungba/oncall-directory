/** Parse a date/time emitted by the API. The API now always appends 'Z' (UTC);
 * defensively treat any naive value as UTC so stale/other strings render consistently. */
export function parseApiDate(s: string): Date {
  return new Date(/Z$|[+-]\d{2}:?\d{2}$/.test(s) ? s : `${s}Z`)
}

/** "4:00 PM – 8:00 PM" (local time) from API start/end strings. */
export function formatTimeRange(start: string, end: string): string {
  const fmt: Intl.DateTimeFormatOptions = { hour: 'numeric', minute: '2-digit' }
  const a = parseApiDate(start).toLocaleTimeString([], fmt)
  const b = parseApiDate(end).toLocaleTimeString([], fmt)
  return `${a} – ${b}`
}

/** Render a date-only value ('YYYY-MM-DD') without shifting by the UTC offset. */
export function formatDateOnly(iso: string): string {
  const [y, m, d] = iso.split('T')[0].split('-').map(Number)
  if (!y || !m || !d) return iso
  return new Date(y, m - 1, d).toLocaleDateString()
}

/** "ends in 2h 14m", "ends in 5m", or "ended". */
export function formatCountdown(end: string, now: Date = new Date()): string {
  const ms = parseApiDate(end).getTime() - now.getTime()
  if (ms <= 0) return 'ended'
  const totalMin = Math.floor(ms / 60_000)
  const h = Math.floor(totalMin / 60)
  const m = totalMin % 60
  return h <= 0 ? `ends in ${m}m` : `ends in ${h}h ${m}m`
}