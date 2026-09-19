import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  parseAdminConsentResult,
  captureAdminConsentResult,
  readAdminConsentResult,
  clearAdminConsentResult,
  submitAdminConsentResult,
  resetAdminConsentSubmission,
  urlWithoutConsentParams,
} from './adminConsent'

/** A stand-in for sessionStorage, so these stay independent of any DOM. */
function fakeStorage(initial: Record<string, string> = {}) {
  const data = { ...initial }
  return {
    getItem: (k: string) => (k in data ? data[k] : null),
    setItem: (k: string, v: string) => { data[k] = v },
    removeItem: (k: string) => { delete data[k] },
    clear: () => { for (const k of Object.keys(data)) delete data[k] },
    key: (i: number) => Object.keys(data)[i] ?? null,
    get length() { return Object.keys(data).length },
  } as Storage
}

describe('parseAdminConsentResult', () => {
  it('reads a successful consent redirect', () => {
    const r = parseAdminConsentResult('?admin_consent=True&tenant=24b3700e-7053-4498-a4e6-b8ebf85dc38c')
    expect(r).toEqual({
      ok: true,
      tenantId: '24b3700e-7053-4498-a4e6-b8ebf85dc38c',
      error: undefined,
      description: undefined,
    })
  })

  // Microsoft sends admin_consent=True on failures too, with the error alongside it. Reading
  // the flag alone would report a refusal as a success.
  it('treats a redirect carrying an error as failure even though admin_consent is True', () => {
    const r = parseAdminConsentResult('?admin_consent=True&error=consent_required&error_description=AADSTS65004%3A+denied')
    expect(r?.ok).toBe(false)
    expect(r?.error).toBe('consent_required')
    expect(r?.description).toBe('AADSTS65004: denied')
  })

  it('ignores a query string that is not a consent redirect', () => {
    expect(parseAdminConsentResult('')).toBeNull()
    expect(parseAdminConsentResult('?code=abc&state=xyz')).toBeNull()
  })

  it('accepts any casing of the flag', () => {
    expect(parseAdminConsentResult('?admin_consent=true')?.ok).toBe(true)
    expect(parseAdminConsentResult('?admin_consent=nonsense')?.ok).toBe(false)
  })
})

describe('capture and read', () => {
  it('survives the boot-to-login-page trip', () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc', storage)
    expect(readAdminConsentResult(storage)?.tenantId).toBe('abc')
  })

  it('stores nothing when the URL is not a consent redirect', () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?code=abc', storage)
    expect(readAdminConsentResult(storage)).toBeNull()
  })

  it('clears on dismiss', () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True', storage)
    clearAdminConsentResult(storage)
    expect(readAdminConsentResult(storage)).toBeNull()
  })

  it('returns null rather than throwing on unreadable stored data', () => {
    const storage = fakeStorage({ adminConsentResult: '{not json' })
    expect(readAdminConsentResult(storage)).toBeNull()
  })
})

describe('the invite token on the redirect', () => {
  it('is carried through parse and capture', () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=deadbeef', storage)
    expect(readAdminConsentResult(storage)?.state).toBe('deadbeef')
  })

  // The invite is spent once it connects, so asking again would be told the link is invalid.
  it('keeps a connection that already succeeded rather than re-posting a spent invite', () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=deadbeef', storage)
    storage.setItem('adminConsentResult', JSON.stringify({
      ok: true, tenantId: 'abc', state: 'deadbeef', delivery: 'done', connected: true, message: 'Connected.',
    }))

    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=deadbeef', storage)

    const stored = readAdminConsentResult(storage)
    expect(stored?.delivery).toBe('done')
    expect(stored?.connected).toBe(true)
  })

  // Both links carry the same invite, and the first trip usually reports "cannot read your
  // directory yet" because the second has not been opened. Arriving again for that invite is
  // the rest of the sequence, not a replay — it has to be posted.
  it('replaces a failed verdict when the second consent link comes back on the same invite', () => {
    const storage = fakeStorage({
      adminConsentResult: JSON.stringify({
        ok: true, tenantId: 'abc', state: 'tok', delivery: 'done', connected: false,
        message: 'OnCall still cannot read your directory.',
      }),
    })

    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=tok', storage)

    expect(readAdminConsentResult(storage)?.delivery).toBeUndefined()
    expect(readAdminConsentResult(storage)?.connected).toBeUndefined()
  })

  it('does replace a stored result when a different consent comes back', () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=one', storage)
    captureAdminConsentResult('?admin_consent=True&tenant=xyz&state=two', storage)
    expect(readAdminConsentResult(storage)?.state).toBe('two')
  })
})

describe('handing the outcome back to the server', () => {
  beforeEach(() => resetAdminConsentSubmission())

  const connected = { connected: true, message: 'Your directory is connected.', subscriptionName: 'Northwood' }

  it('posts the state and stores what the server says, not what the redirect claimed', async () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=tok', storage)
    const post = vi.fn().mockResolvedValue(connected)

    const result = await submitAdminConsentResult(post, storage)

    expect(post).toHaveBeenCalledWith({ state: 'tok', tenant: 'abc', error: undefined, errorDescription: undefined })
    expect(result?.connected).toBe(true)
    expect(result?.subscriptionName).toBe('Northwood')
    expect(readAdminConsentResult(storage)?.delivery).toBe('done')
  })

  // Microsoft's "success" is not OnCall's. A redirect that says admin_consent=True while the
  // directory cannot be read has to report the second thing, or the operator is told the
  // customer is live when their staff are locked out.
  it('reports a refusal even though the redirect said consent succeeded', async () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=tok', storage)

    const result = await submitAdminConsentResult(
      vi.fn().mockResolvedValue({ connected: false, message: 'OnCall still cannot read your directory.' }),
      storage,
    )

    expect(result?.ok).toBe(true)          // what Microsoft said
    expect(result?.connected).toBe(false)  // what is actually true
  })

  it('sends a refused consent too, so the invite records why', async () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&error=access_denied&error_description=cancelled&state=tok', storage)
    const post = vi.fn().mockResolvedValue({ connected: false, message: 'Consent was not granted.' })

    await submitAdminConsentResult(post, storage)

    expect(post).toHaveBeenCalledWith(
      expect.objectContaining({ state: 'tok', error: 'access_denied', errorDescription: 'cancelled' }),
    )
  })

  // The invite is single use: a second post is answered "no longer valid", which would replace
  // a successful connection's message with a false failure.
  it('never posts the same invite twice', async () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=tok', storage)
    const post = vi.fn().mockResolvedValue(connected)

    await submitAdminConsentResult(post, storage)
    resetAdminConsentSubmission()
    await submitAdminConsentResult(post, storage)

    expect(post).toHaveBeenCalledTimes(1)
  })

  it('does not re-post one that was in flight when the page reloaded', async () => {
    const storage = fakeStorage({
      adminConsentResult: JSON.stringify({ ok: true, state: 'tok', delivery: 'sending' }),
    })
    const post = vi.fn().mockResolvedValue(connected)

    await submitAdminConsentResult(post, storage)

    expect(post).not.toHaveBeenCalled()
  })

  it('rewinds to pending when the server could not be reached, so a reload retries', async () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc&state=tok', storage)

    const result = await submitAdminConsentResult(vi.fn().mockRejectedValue(new Error('offline')), storage)

    expect(result?.delivery).toBe('pending')
    expect(result?.connected).toBeUndefined()
  })

  it('leaves a redirect carrying no invite token alone', async () => {
    const storage = fakeStorage()
    captureAdminConsentResult('?admin_consent=True&tenant=abc', storage)
    const post = vi.fn()

    const result = await submitAdminConsentResult(post, storage)

    expect(post).not.toHaveBeenCalled()
    expect(result?.ok).toBe(true)
  })
})

describe('urlWithoutConsentParams', () => {
  it('takes the consent parameters out and leaves the rest of the URL alone', () => {
    expect(urlWithoutConsentParams('https://oncall.test/admin?admin_consent=True&tenant=abc&state=tok&tab=tenants'))
      .toBe('https://oncall.test/admin?tab=tenants')
  })

  it('leaves a URL that carries no consent parameters untouched', () => {
    expect(urlWithoutConsentParams('https://oncall.test/admin')).toBe('https://oncall.test/admin')
  })
})
