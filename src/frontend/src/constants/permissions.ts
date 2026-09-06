/**
 * The permissions an administrator can hand out, and what to call them on screen.
 *
 * Mirrors `Permissions.AssignablePermissions` on the server, which is also what
 * `ParseAssignablePermissionCsv` filters anything submitted down to. Admin.Full, Admin.Scoped
 * and Tenant.Manage are absent on purpose: they are stripped server-side, so offering them here
 * would only produce a control that appears to work and quietly does nothing.
 *
 * Shared by the single-grant form and the bulk grant modal so the two cannot drift.
 */
export const PERMISSION_OPTIONS: { key: string; label: string }[] = [
  { key: 'Schedule.Read', label: 'On-Call Schedule — Read' },
  { key: 'Schedule.Write', label: 'On-Call Schedule — Write' },
  { key: 'Directory.Read', label: 'Directory — Read' },
  { key: 'Directory.Write', label: 'Directory — Write' },
  { key: 'CodeCall.Write', label: 'Code Call — Write' },
]

/** What a fresh grant starts with: enough to see the rota and look someone up. */
export const DEFAULT_GRANT_PERMISSIONS = ['Schedule.Read', 'Directory.Read']

/** How a record was first created, for filtering the accounts list. */
export const SOURCE_FILTERS: { value: string; label: string }[] = [
  { value: '', label: 'Any origin' },
  { value: 'CsvImport', label: 'Uploaded (CSV)' },
  { value: 'Ad', label: 'AD sync' },
  { value: 'Local', label: 'Added manually' },
  { value: 'unknown', label: 'Unknown (legacy)' },
]

/** Legacy rows carry an empty source, so they need their own bucket rather than falling into one. */
export function matchesSource(source: string | undefined, filter: string): boolean {
  if (!filter) return true
  if (filter === 'unknown') return !source
  return source === filter
}
