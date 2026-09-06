import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import BulkResultPanel from './BulkResultPanel'
import { matchesSource } from '@/constants/permissions'
import type { BulkActionResult } from '@/types'

function result(overrides: Partial<BulkActionResult> = {}): BulkActionResult {
  return {
    action: 'delete',
    batchId: 'batch-1',
    requested: 3,
    succeeded: 1,
    blocked: 2,
    skipped: 0,
    notFound: 0,
    grantsRevoked: 1,
    signInsDisabled: 1,
    systemWideGrantsLeft: 0,
    privilegedPrincipals: 0,
    results: [
      { employeeId: 'a', displayName: 'Alice Adams', email: 'alice@x.test', outcome: 'succeeded', grantsRevoked: 1, signInsDisabled: 1, isPrivilegedPrincipal: false },
      { employeeId: 'b', displayName: 'Bob Baker', email: 'bob@x.test', outcome: 'blockedByHistory', message: 'Referenced by shifts.', grantsRevoked: 0, signInsDisabled: 0, isPrivilegedPrincipal: false },
      { employeeId: 'c', displayName: 'Cara Cole', email: 'cara@x.test', outcome: 'blockedByHistory', message: 'Referenced by time-off requests.', grantsRevoked: 0, signInsDisabled: 0, isPrivilegedPrincipal: false },
    ],
    ...overrides,
  }
}

/**
 * A part-blocked batch is the ordinary outcome of a bulk delete, so this panel is the main
 * result surface rather than an error state. What matters is that the refused records are
 * named, and that they can be handed straight back to the selection — because the next thing
 * an admin wants is to deactivate exactly those.
 */
describe('BulkResultPanel', () => {
  it('summarises what happened, grouped by outcome', () => {
    render(<BulkResultPanel result={result()} onDismiss={() => {}} onSelectIds={() => {}} />)

    expect(screen.getByText('1 succeeded')).toBeInTheDocument()
    expect(screen.getAllByText(/2 blocked by history/i).length).toBeGreaterThan(0)
    expect(screen.getByText(/1 permission grants revoked/)).toBeInTheDocument()
  })

  it('names the blocked records and why, once expanded', () => {
    render(<BulkResultPanel result={result()} onDismiss={() => {}} onSelectIds={() => {}} />)

    fireEvent.click(screen.getByRole('button', { name: /2 Blocked by history/i }))

    expect(screen.getByText(/Bob Baker/)).toBeInTheDocument()
    expect(screen.getByText(/Referenced by time-off requests/)).toBeInTheDocument()
  })

  it('hands exactly the blocked records back to the selection', () => {
    const onSelectIds = vi.fn()
    render(<BulkResultPanel result={result()} onDismiss={() => {}} onSelectIds={onSelectIds} />)

    fireEvent.click(screen.getByRole('button', { name: /Keep these 2 selected/ }))

    // Not the succeeded one — that record no longer exists.
    expect(onSelectIds).toHaveBeenCalledWith(['b', 'c'])
  })

  it('says when access was left in place, rather than implying it was removed', () => {
    render(
      <BulkResultPanel
        result={result({ systemWideGrantsLeft: 1, privilegedPrincipals: 2 })}
        onDismiss={() => {}}
        onSelectIds={() => {}}
      />,
    )

    expect(screen.getByText(/only a full administrator can revoke those/i)).toBeInTheDocument()
    expect(screen.getByText(/did not remove that/i)).toBeInTheDocument()
  })

  it('offers no recovery action when nothing was blocked', () => {
    render(
      <BulkResultPanel
        result={result({ blocked: 0, results: [result().results[0]] })}
        onDismiss={() => {}}
        onSelectIds={() => {}}
      />,
    )

    expect(screen.queryByRole('button', { name: /Keep these/ })).not.toBeInTheDocument()
  })
})

/**
 * Legacy rows carry an empty source rather than a missing one, so "unknown" has to be its own
 * bucket — otherwise those records are unreachable from the filter entirely.
 */
describe('matchesSource', () => {
  it('matches everything when no filter is set', () => {
    expect(matchesSource('CsvImport', '')).toBe(true)
    expect(matchesSource(undefined, '')).toBe(true)
  })

  it('matches an exact origin', () => {
    expect(matchesSource('CsvImport', 'CsvImport')).toBe(true)
    expect(matchesSource('Ad', 'CsvImport')).toBe(false)
  })

  it('puts blank and missing origins in the unknown bucket', () => {
    expect(matchesSource('', 'unknown')).toBe(true)
    expect(matchesSource(undefined, 'unknown')).toBe(true)
    expect(matchesSource('Local', 'unknown')).toBe(false)
  })
})
