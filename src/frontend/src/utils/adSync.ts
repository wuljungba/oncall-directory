import type { AdSyncResponse } from '@/services/api'

export interface AdSyncOutcome {
  /** Whether every connected directory was read and its conclusions applied. */
  ok: boolean
  headline: string
  detail?: string
}

/**
 * Turns a directory sync response into something honest to show a person.
 *
 * Three screens report this, and all three used to read `result.synced` — a field the API has
 * never returned. So every sync rendered as a green "AD sync complete: undefined users
 * processed", including a directory that was never consented to and could not be read at all.
 * The failure was in the payload the whole time, under `tenants[].succeeded`.
 *
 * Shared rather than repeated so the wizard, the admin page and settings cannot drift into
 * disagreeing about whether a sync worked.
 */
export function summarizeAdSync(result: AdSyncResponse): AdSyncOutcome {
  const tenants = result.tenants ?? []
  const failed = tenants.filter(t => !t.succeeded)
  const refused = result.deactivationsRefused ?? 0

  const counts =
    `${result.fetched} read, ${result.created} added, ${result.updated} updated, `
    + `${result.deactivated} deactivated`

  // A refusal means the sync completed and declined to apply what it found, because the batch
  // looked like a fault rather than turnover. Never green: somebody has to look at it.
  if (refused > 0) {
    return {
      ok: false,
      headline: `Directory sync held back ${refused} deactivation${refused === 1 ? '' : 's'}.`,
      detail:
        'That many people leaving at once usually means the directory was only partly read. '
        + 'Nobody was deactivated. Check the connection, then run the sync again.',
    }
  }

  if (failed.length > 0) {
    const names = failed
      .map(t => t.tenantName ?? (t.tenantId === null ? 'the main directory' : `subscription ${t.tenantId}`))
      .join(', ')

    return {
      ok: false,
      headline: failed.length === tenants.length
        ? 'The directory could not be read.'
        : `${failed.length} of ${tenants.length} directories could not be read.`,
      detail: `No changes were made for: ${names}. Check that admin consent was granted for that directory.`,
    }
  }

  if (tenants.length === 0) {
    return {
      ok: false,
      headline: 'No directory is connected.',
      detail: 'Set a Directory Tenant ID on a subscription and send its admin the consent links.',
    }
  }

  return {
    ok: true,
    headline: `Directory synced: ${counts}.`,
    detail: result.skipped?.length
      ? `${result.skipped.length} record(s) could not be stored — see the details below.`
      : undefined,
  }
}
