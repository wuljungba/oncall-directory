import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import type { TenantRestoreReport } from '@/types'

const downloadBackup = vi.fn()
const restoreBackup = vi.fn()
const downloadBlob = vi.fn()

vi.mock('@/services/api', () => ({
  tenantsApi: {
    downloadBackup: (id: number) => downloadBackup(id),
    restoreBackup: (id: number, archive: unknown) => restoreBackup(id, archive),
  },
}))
vi.mock('@/utils/download', () => ({ downloadBlob: (b: Blob, n: string) => downloadBlob(b, n) }))

import TenantBackupPanel from './TenantBackupPanel'

/**
 * jsdom's File does not implement Blob.text(), which every browser this app targets has had
 * since 2020. Patched here rather than switching the component to FileReader, because the test
 * environment's gap is not a reason to write older code.
 */
function jsonFile(contents: string, name = 'backup.json'): File {
  const file = new File([contents], name, { type: 'application/json' })
  Object.defineProperty(file, 'text', { value: () => Promise.resolve(contents) })
  return file
}

function report(overrides: Partial<TenantRestoreReport> = {}): TenantRestoreReport {
  return {
    tenantId: 1,
    tenantName: 'Northwood',
    departmentsCreated: 2, departmentsSkipped: 0,
    employeesCreated: 5, employeesSkipped: 1,
    phoneTreesCreated: 1, phoneTreesSkipped: 0,
    phoneTreeNodesCreated: 3, phoneTreeNodesSkipped: 0,
    incidentsInArchive: 10,
    settingsWithheldInArchive: 1,
    notRestored: [
      '10 code-call incident(s) are in this archive and were not written back.',
      '1 sensitive setting(s) were withheld when this archive was exported.',
    ],
    ...overrides,
  }
}

/**
 * A subscription's own copy of its own data.
 *
 * What matters here is honesty about scope. A restore deliberately does less than a backup
 * contains — it never writes incident history back, and secrets were never in the file — so
 * the panel has to say so before someone relies on it, and say what it actually did
 * afterwards. "Restored" read as "everything came back" would be the worst outcome.
 */
describe('TenantBackupPanel', () => {
  beforeEach(() => {
    downloadBackup.mockReset()
    restoreBackup.mockReset()
    downloadBlob.mockReset()
  })

  it('downloads the archive under the name the server chose', async () => {
    downloadBackup.mockResolvedValue({
      blob: new Blob(['{}']),
      filename: 'oncall-backup-Northwood-2026-09-25.json',
    })

    render(<TenantBackupPanel tenantId={1} tenantName="Northwood" />)
    fireEvent.click(screen.getByRole('button', { name: /download backup/i }))

    await waitFor(() => expect(downloadBlob).toHaveBeenCalled())
    // The server knows the subscription name and date; inventing a client-side name loses that.
    expect(downloadBlob.mock.calls[0][1]).toBe('oncall-backup-Northwood-2026-09-25.json')
  })

  it('says a backup failed rather than appearing to succeed', async () => {
    downloadBackup.mockRejectedValue(new Error('Storage unreachable'))

    render(<TenantBackupPanel tenantId={1} tenantName="Northwood" />)
    fireEvent.click(screen.getByRole('button', { name: /download backup/i }))

    // Believing you hold a backup and not holding one is worse than knowing you have none.
    expect(await screen.findByText(/Storage unreachable/)).toBeInTheDocument()
    expect(downloadBlob).not.toHaveBeenCalled()
  })

  it('warns up front about what a restore will not do', () => {
    render(<TenantBackupPanel tenantId={1} tenantName="Northwood" />)

    expect(screen.getByText(/never overwrites or deletes/i)).toBeInTheDocument()
    expect(screen.getByText(/never written back/i)).toBeInTheDocument()
  })

  it('reports what was added and what was already there', async () => {
    restoreBackup.mockResolvedValue(report())

    render(<TenantBackupPanel tenantId={1} tenantName="Northwood" />)
    const input = document.querySelector('input[type="file"]') as HTMLInputElement
    fireEvent.change(input, { target: { files: [jsonFile(JSON.stringify({ formatVersion: 1 }))] } })

    expect(await screen.findByText(/Restored into Northwood/)).toBeInTheDocument()
    expect(screen.getByText('5 added')).toBeInTheDocument()
    // A restore that legitimately skips everything must not read as data loss.
    expect(screen.getByText(/1 already there/)).toBeInTheDocument()
  })

  it('surfaces what was deliberately not restored', async () => {
    restoreBackup.mockResolvedValue(report())

    render(<TenantBackupPanel tenantId={1} tenantName="Northwood" />)
    const input = document.querySelector('input[type="file"]') as HTMLInputElement
    fireEvent.change(input, { target: { files: [jsonFile('{}')] } })

    expect(await screen.findByText(/were not written back/)).toBeInTheDocument()
    expect(screen.getByText(/sensitive setting\(s\) were withheld/)).toBeInTheDocument()
  })

  it('rejects a file that is not a backup, without calling the server', async () => {
    render(<TenantBackupPanel tenantId={1} tenantName="Northwood" />)
    const input = document.querySelector('input[type="file"]') as HTMLInputElement
    fireEvent.change(input, { target: { files: [jsonFile('not json at all', 'notes.txt')] } })

    expect(await screen.findByText(/not a valid OnCall backup/i)).toBeInTheDocument()
    expect(restoreBackup).not.toHaveBeenCalled()
  })
})
