import { describe, it, expect } from 'vitest'
import {
  parseAdminConsentResult,
  captureAdminConsentResult,
  readAdminConsentResult,
  clearAdminConsentResult,
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
