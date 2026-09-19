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
 *
 * What the redirect says is only half the story. When it carries the `state` from an
 * onboarding invite, that is handed back to the server, which checks the invite and reads
 * the directory for real before connecting anything — and its answer, not this one, is what
 * the admin is shown.
 */
export interface AdminConsentResult {
  ok: boolean
  /**
   * Displayed only, never used to authorize anything. A tenant id in a redirect is
   * attacker-supplied — Microsoft's own documentation warns against treating it as proof of
   * anything. The server re-derives it and proves it with a Graph read.
   */
  tenantId?: string
  /**
   * The onboarding invite's token, round-tripped through OAuth `state`. Absent on a consent
   * link built the old way, which names a directory instead of carrying an invite.
   */
  state?: string
  error?: string
  description?: string

  // ── The server's verdict, once it has been asked ──

  /**
   * How far the hand-back has got. `sending` is persisted BEFORE the request goes out: the
   * invite is single use, so a reload mid-flight must not post it a second time and be told
   * the link is spent when it in fact just worked.
   */
  delivery?: 'pending' | 'sending' | 'done'
  /** Whether the directory is now connected. The server's answer, not the redirect's claim. */
  connected?: boolean
  /** What to show the admin, in the server's words. */
  message?: string
  subscriptionName?: string
  /** True when the directory reads but its organisation details do not — one more consent. */
  needsReconsent?: boolean
}

/** What the server answers when handed a consent outcome. */
export interface ConsentCompletion {
  connected: boolean
  message: string
  subscriptionName?: string
  directoryName?: string
  needsReconsent?: boolean
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
    state: params.get('state') || undefined,
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

function write(result: AdminConsentResult, storage: Storage | undefined): void {
  try {
    storage?.setItem(STORAGE_KEY, JSON.stringify(result))
  } catch {
    // Nothing to do: the banner is a courtesy, and consent already succeeded or failed
    // server-side regardless of what we can show.
  }
}

/** The parameters Microsoft adds to the redirect, and the only ones stripped from it. */
const CONSENT_PARAMS = ['admin_consent', 'tenant', 'state', 'error', 'error_description']

/**
 * The same URL with the consent parameters taken out, leaving anything else alone.
 *
 * Once the outcome is in storage the address bar is a liability: a reload replays it, and a
 * replayed consent posts an invite that is single use, which answers "that link is no longer
 * valid" about a directory that connected perfectly well a moment ago. Taking the parameters
 * out makes a reload an ordinary page load, while a genuine second trip through Microsoft —
 * the customer's admin opening the second of the two links — still arrives carrying them.
 */
export function urlWithoutConsentParams(href: string): string {
  const url = new URL(href)
  for (const p of CONSENT_PARAMS) url.searchParams.delete(p)
  return url.toString()
}

/** True when a consent outcome was found and stored. */
export function captureAdminConsentResult(search: string, storage = safeStorage()): boolean {
  const result = parseAdminConsentResult(search)
  if (!result || !storage) return false

  // Both consent links carry the SAME invite, so arriving twice for one invite is the normal
  // two-link sequence, not a replay — the first trip usually reports "cannot read your
  // directory yet" precisely because the second link has not been opened. That has to be
  // overwritten and re-posted. What must survive is a connection that already succeeded:
  // the invite is spent, and asking again would report it as invalid.
  const existing = readAdminConsentResult(storage)
  if (existing && existing.state === result.state && existing.connected === true) return true

  write(result, storage)
  return true
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

/**
 * In-flight hand-back, so React's double-invoked effects post once rather than twice. The
 * persisted `sending` covers reloads; this covers the same page life.
 */
let inFlight: Promise<AdminConsentResult | null> | null = null

/**
 * Hands the consent outcome back to the server and stores what it says.
 *
 * The redirect on its own proves nothing — it is attacker-supplied — so the admin is not
 * told "connected" until the server has matched the invite and read the directory. A
 * redirect carrying no invite token is left alone: there is nothing to match it against,
 * and the banner falls back to reporting what Microsoft said.
 *
 * Failures to reach the server rewind to `pending` so a reload tries again; a request that
 * was sent is never sent twice, because the invite behind it is single use.
 */
export function submitAdminConsentResult(
  post: (body: { state: string; tenant?: string; error?: string; errorDescription?: string }) => Promise<ConsentCompletion>,
  storage = safeStorage(),
): Promise<AdminConsentResult | null> {
  if (inFlight) return inFlight

  const stored = readAdminConsentResult(storage)
  if (!stored || !stored.state || stored.delivery === 'done' || stored.delivery === 'sending') {
    return Promise.resolve(stored)
  }

  const sending: AdminConsentResult = { ...stored, delivery: 'sending' }
  write(sending, storage)

  inFlight = (async () => {
    try {
      const reply = await post({
        state: stored.state!,
        tenant: stored.tenantId,
        error: stored.error,
        errorDescription: stored.description,
      })
      const done: AdminConsentResult = {
        ...stored,
        delivery: 'done',
        connected: reply.connected,
        message: reply.message,
        subscriptionName: reply.subscriptionName,
        needsReconsent: reply.needsReconsent,
      }
      write(done, storage)
      return done
    } catch {
      // Unreachable server, not a refusal: leave it posted-able rather than reporting a
      // failure that never happened.
      const rewound: AdminConsentResult = { ...stored, delivery: 'pending' }
      write(rewound, storage)
      return rewound
    } finally {
      inFlight = null
    }
  })()

  return inFlight
}

/** Test seam: forgets any in-flight hand-back between cases. */
export function resetAdminConsentSubmission(): void {
  inFlight = null
}
