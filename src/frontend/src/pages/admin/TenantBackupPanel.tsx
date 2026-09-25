import { useRef, useState } from 'react'
import { AlertTriangle, Download, Info, Upload } from 'lucide-react'
import { tenantsApi } from '@/services/api'
import { downloadBlob } from '@/utils/download'
import type { TenantRestoreReport } from '@/types'

/**
 * Lets one subscription take its own data out, and put it back.
 *
 * The nightly database backups protect the deployment, not a customer: every subscription
 * shares one database, so a point-in-time restore rolls all of them back together and can
 * never be the answer to "we deleted our directory by mistake". This is the per-customer
 * answer — and for a subscription whose staff were uploaded from a spreadsheet rather than
 * synced from Entra, it is the only one, because nothing upstream holds a second copy.
 */
export default function TenantBackupPanel({ tenantId, tenantName }: {
  tenantId: number
  tenantName: string
}) {
  const fileInput = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState<'backup' | 'restore' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [report, setReport] = useState<TenantRestoreReport | null>(null)

  async function handleBackup() {
    setBusy('backup')
    setError(null)
    setReport(null)
    try {
      const { blob, filename } = await tenantsApi.downloadBackup(tenantId)
      downloadBlob(blob, filename)
    } catch (err) {
      // Never silent: someone who believes they hold a backup and does not is worse off than
      // someone who knows they have none.
      setError(err instanceof Error ? err.message : 'Could not build the backup.')
    } finally {
      setBusy(null)
    }
  }

  async function handleRestore(file: File) {
    setBusy('restore')
    setError(null)
    setReport(null)
    try {
      const text = await file.text()
      let archive: unknown
      try {
        archive = JSON.parse(text)
      } catch {
        throw new Error('That file is not a valid OnCall backup — it could not be read as JSON.')
      }
      setReport(await tenantsApi.restoreBackup(tenantId, archive))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'The restore failed.')
    } finally {
      setBusy(null)
      // Cleared so the same file can be chosen again after a failure.
      if (fileInput.current) fileInput.current.value = ''
    }
  }

  return (
    <div className="mt-3 rounded-lg bg-gray-800/40 border border-gray-800 p-4 space-y-3">
      <div className="flex items-start justify-between gap-3 flex-wrap">
        <div className="min-w-0">
          <p className="text-sm font-medium">Data backup</p>
          <p className="text-xs text-gray-500 mt-0.5 max-w-xl leading-relaxed">
            Downloads everything {tenantName} owns — people, departments, schedules, shifts,
            code trees and incident history — as one JSON file you can keep. The database
            backups cover the whole deployment and cannot be restored for one subscription
            alone, so this is the only copy that is yours.
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <button
            type="button"
            onClick={handleBackup}
            disabled={busy !== null}
            className="flex items-center gap-1.5 text-xs px-3 py-1.5 rounded-lg bg-amber-600 hover:bg-amber-700 text-white disabled:opacity-40"
          >
            <Download className="w-3.5 h-3.5" />
            {busy === 'backup' ? 'Preparing…' : 'Download backup'}
          </button>
          <button
            type="button"
            onClick={() => fileInput.current?.click()}
            disabled={busy !== null}
            className="flex items-center gap-1.5 text-xs px-3 py-1.5 rounded-lg bg-gray-800 hover:bg-gray-700 text-gray-300 disabled:opacity-40"
          >
            <Upload className="w-3.5 h-3.5" />
            {busy === 'restore' ? 'Restoring…' : 'Restore from file'}
          </button>
          <input
            ref={fileInput}
            type="file"
            accept="application/json,.json"
            className="hidden"
            onChange={e => {
              const file = e.target.files?.[0]
              if (file) handleRestore(file)
            }}
          />
        </div>
      </div>

      {/* Stated before anyone uses it, not discovered afterwards. */}
      <p className="text-[11px] text-gray-600 flex items-start gap-1.5 leading-relaxed">
        <Info className="w-3.5 h-3.5 shrink-0 mt-px" />
        <span>
          Restoring only adds what is missing — it never overwrites or deletes what is already
          here, so it is safe to run twice. Incident history is included in the file for your
          records but is never written back, and passwords and API secrets are left out of the
          file entirely.
        </span>
      </p>

      {error && (
        <div className="flex items-start gap-2 text-xs text-red-400 bg-red-600/10 rounded-lg px-3 py-2">
          <AlertTriangle className="w-4 h-4 shrink-0 mt-px" />
          <span className="break-words">{error}</span>
        </div>
      )}

      {report && (
        <div className="text-xs bg-gray-900 border border-gray-800 rounded-lg p-3 space-y-2">
          <p className="text-gray-300 font-medium">Restored into {report.tenantName}</p>
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2 text-[11px]">
            <Tally label="People" created={report.employeesCreated} skipped={report.employeesSkipped} />
            <Tally label="Departments" created={report.departmentsCreated} skipped={report.departmentsSkipped} />
            <Tally label="Code trees" created={report.phoneTreesCreated} skipped={report.phoneTreesSkipped} />
            <Tally label="Tree steps" created={report.phoneTreeNodesCreated} skipped={report.phoneTreeNodesSkipped} />
          </div>
          {report.notRestored.length > 0 && (
            <ul className="space-y-1 pt-1 border-t border-gray-800">
              {report.notRestored.map(line => (
                <li key={line} className="text-[11px] text-amber-500/90 flex items-start gap-1.5">
                  <span aria-hidden>·</span><span>{line}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}

/**
 * "Added" and "already there" side by side. A bare count of what was written reads as loss
 * when a restore legitimately skips everything because nothing was missing.
 */
function Tally({ label, created, skipped }: { label: string; created: number; skipped: number }) {
  return (
    <div className="bg-gray-800/60 rounded px-2 py-1.5">
      <p className="text-gray-500">{label}</p>
      <p className="text-gray-300">
        <span className={created > 0 ? 'text-green-400' : ''}>{created} added</span>
        <span className="text-gray-600"> · {skipped} already there</span>
      </p>
    </div>
  )
}
