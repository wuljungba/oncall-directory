import { describe, it, expect } from 'vitest'
import { summarizeAdSync } from './adSync'
import type { AdSyncResponse, AdSyncTenantResult } from '@/services/api'

function tenant(overrides: Partial<AdSyncTenantResult> = {}): AdSyncTenantResult {
  return {
    tenantId: 1,
    tenantName: 'Northwood Medical',
    succeeded: true,
    fetched: 10,
    created: 1,
    updated: 9,
    deactivated: 0,
    pagesRead: 1,
    mode: 'full',
    deactivationsRefused: 0,
    needsAttention: false,
    ...overrides,
  }
}

function response(overrides: Partial<AdSyncResponse> = {}): AdSyncResponse {
  return {
    fetched: 10,
    created: 1,
    updated: 9,
    deactivated: 0,
    deactivationsRefused: 0,
    failedDirectories: 0,
    skipped: [],
    tenants: [tenant()],
    ...overrides,
  }
}

describe('summarizeAdSync', () => {
  it('reports a clean run with real counts', () => {
    const outcome = summarizeAdSync(response())

    expect(outcome.ok).toBe(true)
    expect(outcome.headline).toContain('10 read')
    expect(outcome.headline).not.toContain('undefined')
  })

  // The bug this exists to prevent: the API answers 200 for a directory it could not read,
  // and every screen rendered that as success.
  it('refuses to call a failed directory a success', () => {
    const outcome = summarizeAdSync(response({
      failedDirectories: 1,
      tenants: [tenant({ succeeded: false, fetched: 0, created: 0, updated: 0 })],
    }))

    expect(outcome.ok).toBe(false)
    expect(outcome.headline).toContain('could not be read')
  })

  it('names which directories failed when only some did', () => {
    const outcome = summarizeAdSync(response({
      failedDirectories: 1,
      tenants: [
        tenant({ tenantId: 1, tenantName: 'Northwood Medical' }),
        tenant({ tenantId: 2, tenantName: 'Mercy General', succeeded: false }),
      ],
    }))

    expect(outcome.ok).toBe(false)
    expect(outcome.headline).toContain('1 of 2')
    expect(outcome.detail).toContain('Mercy General')
  })

  it('treats held-back deactivations as something to look at, not a success', () => {
    const outcome = summarizeAdSync(response({
      deactivationsRefused: 90,
      tenants: [tenant({ deactivationsRefused: 90, needsAttention: true })],
    }))

    expect(outcome.ok).toBe(false)
    expect(outcome.headline).toContain('90')
    expect(outcome.detail).toContain('Nobody was deactivated')
  })

  it('says so when nothing is connected at all', () => {
    const outcome = summarizeAdSync(response({ tenants: [], fetched: 0, created: 0, updated: 0 }))

    expect(outcome.ok).toBe(false)
    expect(outcome.headline).toContain('No directory is connected')
  })

  it('mentions records that could not be stored', () => {
    const outcome = summarizeAdSync(response({ skipped: ['someone — no usable email address'] }))

    expect(outcome.ok).toBe(true)
    expect(outcome.detail).toContain('1 record(s) could not be stored')
  })
})
