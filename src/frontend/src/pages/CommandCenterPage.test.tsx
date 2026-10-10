import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { PhoneTreeEvent } from '@/types'

const getResolved = vi.fn()
const getActive = vi.fn(() => Promise.resolve([] as PhoneTreeEvent[]))
const getTrees = vi.fn(() => Promise.resolve([] as unknown[]))
const createEvent = vi.fn()

vi.mock('@/services/api', () => ({
  commandCenterApi: {
    getActive: () => getActive(),
    getResolved: () => getResolved(),
    acknowledgeEvent: vi.fn(),
    resolveEvent: vi.fn(),
    addDispatchStep: vi.fn(),
    addDebriefNote: vi.fn(),
  },
  phoneTreesApi: { getAll: () => getTrees(), create: vi.fn(), createEvent: (...a: unknown[]) => createEvent(...a) },
  codeCallLocationsApi: { getAll: () => Promise.resolve([]) },
  departmentsApi: { getAll: () => Promise.resolve([]) },
}))

vi.mock('@/hooks/useSignalR', () => ({ useSignalR: () => ({ isConnected: false, lastEvent: null }) }))
vi.mock('@/components/ui/Dialog', () => ({ useDialog: () => ({ prompt: vi.fn(), confirm: vi.fn() }) }))

import CommandCenterPage from './CommandCenterPage'

function incident(overrides: Partial<PhoneTreeEvent> = {}): PhoneTreeEvent {
  return {
    id: 10,
    phoneTreeId: 1,
    startedAt: '2026-09-08T13:58:00Z',
    endedAt: '2026-09-08T13:58:26Z',
    status: 'completed',
    location: '3 N',
    participants: [],
    phoneTree: { id: 1, name: 'Code red', treeType: 'code-red', nodes: [] },
    ...overrides,
  }
}

/**
 * The incident history is the record of who was paged during an emergency, so the column that
 * names the operator is not decoration.
 *
 * It replaced an Outcome column that no code path ever wrote, so it read "Not recorded" on
 * every row forever. The replacement has a harder obligation: it must say who raised the code
 * when that is known, and must not imply anyone when it is not — incidents raised before the
 * account was captured, and those the EHR webhook raises with no operator at all, have to read
 * as unattributed rather than blank or guessed.
 */
describe('CommandCenterPage — Triggered by', () => {
  beforeEach(() => {
    getResolved.mockReset()
  })

  it('names the account that raised the code, with its address', async () => {
    getResolved.mockResolvedValue([
      incident({ initiatedByName: 'Divine Yisa', initiatedByEmail: 'divine@hospital.test' }),
    ])

    render(<CommandCenterPage />)

    expect(await screen.findByText('Divine Yisa')).toBeInTheDocument()
    expect(screen.getByText('divine@hospital.test')).toBeInTheDocument()
  })

  it('shows the column instead of the outcome that was never recorded', async () => {
    getResolved.mockResolvedValue([incident({ initiatedByName: 'Divine Yisa' })])

    render(<CommandCenterPage />)

    expect(await screen.findByText('Triggered by')).toBeInTheDocument()
    expect(screen.queryByText('Outcome')).not.toBeInTheDocument()
    expect(screen.queryByText('Not recorded')).not.toBeInTheDocument()
  })

  // The honest answer for #9 and #10 in the live console, raised before the capture was fixed.
  it('says so plainly when no account was recorded, rather than guessing', async () => {
    getResolved.mockResolvedValue([incident({ requestedByName: 'Dan' })])

    render(<CommandCenterPage />)

    expect(await screen.findByText('Not captured')).toBeInTheDocument()
  })

  /**
   * A name alone still attributes the incident. The address is additional evidence, not a
   * precondition — older rows carry one without the other.
   */
  it('renders a name with no address', async () => {
    getResolved.mockResolvedValue([incident({ initiatedByName: 'Charge Nurse' })])

    render(<CommandCenterPage />)

    expect(await screen.findByText('Charge Nurse')).toBeInTheDocument()
    expect(screen.queryByText('Not captured')).not.toBeInTheDocument()
  })

  /**
   * Falls back to the linked employee when no name was pinned. It used to join firstName and
   * lastName by hand, so a department-style contact — a unit reached by phone, which carries
   * only a displayName — rendered as an empty string.
   */
  it('resolves a department contact that has only a display name', async () => {
    getResolved.mockResolvedValue([
      incident({
        initiatedBy: { displayName: 'Nursing Station 3N' } as PhoneTreeEvent['initiatedBy'],
      }),
    ])

    render(<CommandCenterPage />)

    expect(await screen.findByText('Nursing Station 3N')).toBeInTheDocument()
  })

  it('no longer duplicates the operator in the People column', async () => {
    getResolved.mockResolvedValue([
      incident({ initiatedByName: 'Divine Yisa', requestedByName: 'Dan', notifiedByName: 'Nursing' }),
    ])

    render(<CommandCenterPage />)

    await waitFor(() => expect(screen.getByText('Divine Yisa')).toBeInTheDocument())

    // People keeps Reported and Notified; Triggered moved to its own column.
    expect(screen.getByText(/Reported/)).toBeInTheDocument()
    expect(screen.getByText(/Notified/)).toBeInTheDocument()
    expect(screen.queryByText(/^Triggered $/)).not.toBeInTheDocument()
  })
})

/**
 * The operator who raises a code must see it land in the Active column. That used to depend on
 * the IncidentCreated SignalR broadcast alone, so with the hub down (useSignalR is mocked as
 * disconnected here) the code was raised, paged, and never appeared on the console that raised
 * it, leaving the operator unable to tell whether it had gone out at all.
 */
describe('CommandCenterPage — raising a code with no live connection', () => {
  const codeBlue = { id: 1, name: 'Code Blue — Cardiac Arrest', treeType: 'code-blue', nodes: [] }

  beforeEach(() => {
    getResolved.mockReset().mockResolvedValue([])
    getActive.mockReset().mockResolvedValue([])
    getTrees.mockReset().mockResolvedValue([codeBlue])
    createEvent.mockReset()
  })

  it('shows the new incident as active without waiting for a broadcast', async () => {
    const raised = incident({
      id: 42, status: 'active', endedAt: undefined, location: 'ICU — Bay 4', phoneTree: codeBlue,
    })
    createEvent.mockResolvedValue(raised)

    render(<CommandCenterPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Activate Code/ }))
    getActive.mockResolvedValue([raised])
    await userEvent.click(screen.getByRole('button', { name: /Dispatch Now/ }))

    expect(createEvent).toHaveBeenCalledWith(1, expect.objectContaining({ confirm: true }))
    expect(await screen.findByText('#42 — Code Blue — Cardiac Arrest')).toBeInTheDocument()
  })
})
