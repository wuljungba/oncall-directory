/**
 * The outcome of a customer's Entra admin granting consent, carried back on the redirect.
 *
 * Their browser has no OnCall session, so the `/admin` redirect target bounces to `/login`
 * and the router drops the query string with it: the admin did what we asked and saw a bare
 * sign-in page, with nothing saying whether it worked. So the outcome is captured from the
 * URL at boot, before the router runs, and shown on the sign-in page.
 *
 * Microsoft sends `admin_consent=True` on BOTH outcomes and adds `error` /
 * `error_description` on failure, so success is "the parameter is present and there is no
 * error" rather than "the parameter is True".
 */
export interface AdminConsentResult {
  ok: boolean
  /**
   * Displayed only, never used to authorize anything. A tenant id in a redirect is
   * attacker-supplied — Microsoft's own documentation warns against treating it as proof of
   * anything.
   */
  tenantId?: string
  error?: string
  description?: string
}

const STORAGE_KEY = 'adminConsentResult'

/** Reads the outcome out of a query string. Returns null when this was not a consent redirect. */
export function parseAdminConsentResult(search: string): AdminConsentResult | null {
  const params = new URLSearchParams(search)
  if (!params.has('admin_consent')) return null

  const error = params.get('error') || undefined
  return {
    ok: !error && (params.get('admin_consent') || '').toLowerCase() === 'true',
    tenantId: params.get('tenant') || undefined,
    error,
    description: params.get('error_description') || undefined,
  }
}

/**
 * sessionStorage, or undefined where touching it throws (some privacy modes throw on access
 * rather than returning empty). Never let a banner stop the app from booting.
 */
function safeStorage(): Storage | undefined {
  try {
    return window.sessionStorage
  } catch {
    return undefined
  }
}

export function captureAdminConsentResult(search: string, storage = safeStorage()): void {
  const result = parseAdminConsentResult(search)
  if (!result || !storage) return
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify(result))
  } catch {
    // Nothing to do: the banner is a courtesy, and consent already succeeded or failed
    // server-side regardless of what we can show.
  }
}

export function readAdminConsentResult(storage = safeStorage()): AdminConsentResult | null {
  if (!storage) return null
  try {
    const raw = storage.getItem(STORAGE_KEY)
    return raw ? (JSON.parse(raw) as AdminConsentResult) : null
  } catch {
    return null
  }
}

export function clearAdminConsentResult(storage = safeStorage()): void {
  try {
    storage?.removeItem(STORAGE_KEY)
  } catch {
    // Same reasoning as capture: never throw out of a dismiss handler.
  }
}
