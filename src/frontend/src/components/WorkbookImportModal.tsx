import { useState, useRef, type ReactNode } from 'react'
import {
  Upload, X, AlertTriangle, Check, FileText, ChevronRight, ChevronLeft, Info,
} from 'lucide-react'
import { importApi, authenticatedBlob, type ImportJobPreview, type ImportRowPreview } from '@/services/api'
import { downloadBlob } from '@/utils/download'

/**
 * The fields a column can be mapped to. Kept in step with the importer's canonical names;
 * an unlisted column is ignored, which is exactly what an empty mapping means.
 */
const FIELDS: { value: string; label: string }[] = [
  { value: '', label: 'Ignore this column' },
  { value: 'firstName', label: 'First name' },
  { value: 'lastName', label: 'Last name' },
  { value: 'name', label: 'Full name (one column)' },
  { value: 'displayName', label: 'Unit / department name' },
  { value: 'email', label: 'Email' },
  { value: 'title', label: 'Title' },
  { value: 'credentials', label: 'Credentials' },
  { value: 'officePhone', label: 'Office phone' },
  { value: 'mobilePhone', label: 'Mobile phone' },
  { value: 'extension', label: 'Extension' },
  { value: 'officeLocation', label: 'Location' },
  { value: 'department', label: 'Department (name)' },
  { value: 'departmentId', label: 'Department (id)' },
  { value: 'contactType', label: 'Contact type' },
  { value: 'azureAdObjectId', label: 'Entra object id' },
]

type Step = 'upload' | 'sheets' | 'mapping' | 'review'

/**
 * A workbook of unit rosters, imported in the order the questions actually arise: what is
 * in this file, what do its columns mean, what is about to happen, and only then — do it.
 *
 * The single-step modal could not ask any of those. It read the first sheet, guessed every
 * column, and either wrote everything or refused the whole file over one bad row.
 */
export default function WorkbookImportModal({
  isOpen,
  onClose,
  onCommitted,
  tenantId,
  extra,
}: {
  isOpen: boolean
  onClose: () => void
  onCommitted: () => void
  tenantId?: number
  /** Optional content above the drop zone, e.g. a subscription picker. */
  extra?: ReactNode
}) {
  const fileInputRef = useRef<HTMLInputElement>(null)

  const [step, setStep] = useState<Step>('upload')
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<ImportJobPreview | null>(null)
  const [activeSheet, setActiveSheet] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [committed, setCommitted] = useState<number | null>(null)

  if (!isOpen) return null

  function reset() {
    setStep('upload')
    setFile(null)
    setPreview(null)
    setActiveSheet(null)
    setError(null)
    setCommitted(null)
  }

  function closeAll() {
    reset()
    onClose()
  }

  function acceptFile(candidate: File) {
    const name = candidate.name.toLowerCase()
    if (name.endsWith('.xls')) {
      setError(
        'Legacy .xls workbooks are not supported. Open it in Excel and choose ' +
        'File > Save As > Excel Workbook (.xlsx), then upload that.',
      )
      return
    }
    if (!name.endsWith('.csv') && !name.endsWith('.xlsx')) {
      setError(`"${candidate.name}" is not a spreadsheet. Upload a .csv or .xlsx file.`)
      return
    }
    setFile(candidate)
    setError(null)
  }

  async function run<T>(work: () => Promise<T>): Promise<T | null> {
    setBusy(true)
    setError(null)
    try {
      return await work()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.')
      return null
    } finally {
      setBusy(false)
    }
  }

  async function handleUpload() {
    if (!file) return
    const result = await run(() => importApi.createJob(file, tenantId))
    if (!result) return

    setPreview(result)
    setActiveSheet(result.sheets[0]?.name ?? null)
    // A single-sheet file has nothing to choose between, so that step is skipped.
    setStep(result.sheets.length > 1 ? 'sheets' : 'mapping')
  }

  async function toggleSheet(name: string) {
    if (!preview) return
    const excluded = preview.sheets.filter(s => !s.included).map(s => s.name)
    const next = excluded.includes(name)
      ? excluded.filter(s => s !== name)
      : [...excluded, name]

    const result = await run(() =>
      importApi.updateMapping(preview.jobId, {
        sheetName: name,
        columns: {},
        excludedSheets: next,
      }),
    )
    if (result) setPreview(result)
  }

  async function changeMapping(column: string, field: string, applyToAll: boolean) {
    if (!preview || !activeSheet) return
    const result = await run(() =>
      importApi.updateMapping(preview.jobId, {
        sheetName: activeSheet,
        columns: { [column]: field },
        applyToAllSheets: applyToAll,
      }),
    )
    if (result) setPreview(result)
  }

  async function changeResolution(row: ImportRowPreview, resolution: 'create' | 'merge' | 'skip') {
    if (!preview) return
    const result = await run(() => importApi.setRowResolution(preview.jobId, row.id, resolution))
    if (result) setPreview(result)
  }

  async function handleCommit() {
    if (!preview) return
    const result = await run(() => importApi.commitJob(preview.jobId))
    if (!result) return

    if (!result.isValid) {
      setError(result.errors.join(' '))
      const refreshed = await run(() => importApi.getJob(preview.jobId))
      if (refreshed) setPreview(refreshed)
      return
    }

    setCommitted(result.imported)
    onCommitted()
  }

  const sheet = preview?.sheets.find(s => s.name === activeSheet) ?? null
  const problemRows = preview?.rows.filter(r => r.included && r.errorReason) ?? []
  const reviewRows = preview?.rows.filter(r => r.included && !r.errorReason && r.reviewReason) ?? []
  const mergeRows = preview?.rows.filter(r => r.included && r.resolution === 'merge') ?? []

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={closeAll}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-3xl mx-4 max-h-[90vh] overflow-y-auto"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">Import directory</h2>
          <button onClick={closeAll} className="p-1 hover:bg-gray-800 rounded-lg transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>

        {preview && committed === null && (
          <div className="flex items-center gap-2 px-5 py-3 border-b border-gray-800 text-xs text-gray-500">
            {(['sheets', 'mapping', 'review'] as Step[]).map((name, i) => (
              <span key={name} className="flex items-center gap-2">
                {i > 0 && <ChevronRight className="w-3 h-3 text-gray-700" />}
                <span className={step === name ? 'text-amber-500 font-medium' : ''}>
                  {name === 'sheets' ? 'Sheets' : name === 'mapping' ? 'Columns' : 'Review'}
                </span>
              </span>
            ))}
          </div>
        )}

        <div className="p-5 space-y-4">
          {error && (
            <div className="flex items-start gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
              <span>{error}</span>
            </div>
          )}

          {committed !== null && (
            <div className="flex items-center gap-2 text-sm text-green-400 bg-green-600/10 rounded-lg px-4 py-3">
              <Check className="w-4 h-4 flex-shrink-0" />
              {committed} {committed === 1 ? 'entry' : 'entries'} imported.
            </div>
          )}

          {/* ── Upload ── */}
          {step === 'upload' && committed === null && (
            <>
              <p className="text-sm text-gray-500">
                Upload a CSV or Excel workbook. Every sheet is read — you choose which ones to
                import and what their columns mean before anything is saved.
              </p>
              {extra && <div className="rounded-lg bg-gray-800/40 p-3">{extra}</div>}
              <div
                onDrop={(e) => {
                  e.preventDefault()
                  const dropped = e.dataTransfer.files[0]
                  if (dropped) acceptFile(dropped)
                }}
                onDragOver={(e) => e.preventDefault()}
                onClick={() => fileInputRef.current?.click()}
                className={`border-2 border-dashed rounded-xl p-8 text-center cursor-pointer transition-colors ${
                  file ? 'border-amber-600 bg-amber-600/5' : 'border-gray-700 hover:border-amber-600/50'
                }`}
              >
                <input
                  ref={fileInputRef}
                  type="file"
                  accept=".csv,.xlsx"
                  className="hidden"
                  onChange={(e) => {
                    const chosen = e.target.files?.[0]
                    if (chosen) acceptFile(chosen)
                  }}
                />
                {file ? (
                  <div className="flex items-center justify-center gap-3">
                    <FileText className="w-8 h-8 text-amber-500" />
                    <div className="text-left">
                      <p className="text-sm font-medium">{file.name}</p>
                      <p className="text-xs text-gray-500">{(file.size / 1024).toFixed(1)} KB</p>
                    </div>
                  </div>
                ) : (
                  <>
                    <Upload className="w-8 h-8 text-gray-600 mx-auto mb-2" />
                    <p className="text-sm text-gray-500">Drop a CSV or Excel (.xlsx) file here, or click to browse</p>
                  </>
                )}
              </div>
            </>
          )}

          {/* ── Sheets ── */}
          {step === 'sheets' && preview && committed === null && (
            <>
              <p className="text-sm text-gray-500">
                {preview.sheetCount} sheets, {preview.totalRows} rows. Untick any you do not want.
              </p>
              <div className="space-y-2">
                {preview.sheets.map(s => (
                  <label
                    key={s.name}
                    className="flex items-center gap-3 bg-gray-800/40 rounded-lg px-4 py-3 cursor-pointer hover:bg-gray-800/70"
                  >
                    <input
                      type="checkbox"
                      checked={s.included}
                      onChange={() => toggleSheet(s.name)}
                      className="accent-amber-600"
                    />
                    <span className="text-sm font-medium flex-1">{s.name}</span>
                    <span className="text-xs text-gray-500">
                      {s.rowCount} {s.rowCount === 1 ? 'row' : 'rows'}
                    </span>
                  </label>
                ))}
              </div>
            </>
          )}

          {/* ── Mapping ── */}
          {step === 'mapping' && preview && sheet && committed === null && (
            <>
              {preview.sheets.length > 1 && (
                <div className="flex gap-1 flex-wrap">
                  {preview.sheets.filter(s => s.included).map(s => (
                    <button
                      key={s.name}
                      onClick={() => setActiveSheet(s.name)}
                      className={`px-3 py-1 rounded-lg text-xs transition-colors ${
                        s.name === activeSheet
                          ? 'bg-amber-600 text-white'
                          : 'bg-gray-800 text-gray-400 hover:bg-gray-700'
                      }`}
                    >
                      {s.name}
                    </button>
                  ))}
                </div>
              )}

              <p className="text-sm text-gray-500">
                What each column means. Everyday headings are recognised already — change any
                that were read wrongly, and ignore the ones you do not need.
              </p>

              <div className="space-y-2">
                {sheet.columns.map(c => (
                  <div key={c.column} className="flex items-center gap-3">
                    <div className="w-1/3 min-w-0">
                      <p className="text-sm truncate" title={c.column}>{c.column}</p>
                      <p className="text-xs text-gray-600 truncate">
                        {sheet.sampleRows[0]?.[c.column] || '—'}
                      </p>
                    </div>
                    <select
                      value={FIELDS.some(f => f.value === c.field) ? c.field : ''}
                      onChange={(e) => changeMapping(c.column, e.target.value, false)}
                      className="flex-1 bg-gray-800 border border-gray-700 rounded-lg px-3 py-2 text-sm focus:outline-none focus:border-amber-600"
                    >
                      {FIELDS.map(f => (
                        <option key={f.value} value={f.value}>{f.label}</option>
                      ))}
                    </select>
                    {preview.sheets.filter(s => s.included).length > 1 && (
                      <button
                        onClick={() => changeMapping(c.column, c.field, true)}
                        className="text-xs text-gray-500 hover:text-amber-500 whitespace-nowrap"
                        title="Use this mapping on every sheet"
                      >
                        All sheets
                      </button>
                    )}
                  </div>
                ))}
              </div>
            </>
          )}

          {/* ── Review ── */}
          {step === 'review' && preview && committed === null && (
            <>
              <div className="grid grid-cols-3 gap-3">
                <Stat label="Ready" value={preview.readyCount} tone="text-green-400" />
                <Stat label="Will update" value={preview.mergeCount} tone="text-amber-400" />
                <Stat label="Problems" value={preview.errorCount} tone="text-red-400" />
              </div>

              {mergeRows.length > 0 && (
                <div className="space-y-2">
                  <p className="text-sm text-gray-400">
                    Already in the directory — these update the existing entry rather than adding
                    a second one.
                  </p>
                  <div className="max-h-40 overflow-y-auto space-y-1">
                    {mergeRows.map(r => (
                      <div key={r.id} className="flex items-center gap-2 text-xs bg-gray-800/40 rounded-lg px-3 py-2">
                        <span className="flex-1 text-gray-400">
                          {r.sheetName} row {r.sourceRow}
                          <span className="text-gray-600"> · matched on {r.matchedOn}</span>
                        </span>
                        <select
                          value={r.resolution}
                          onChange={(e) => changeResolution(r, e.target.value as 'create' | 'merge' | 'skip')}
                          className="bg-gray-800 border border-gray-700 rounded px-2 py-1 focus:outline-none focus:border-amber-600"
                        >
                          <option value="merge">Update existing</option>
                          <option value="create">Add as new</option>
                          <option value="skip">Skip</option>
                        </select>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {reviewRows.length > 0 && (
                <div className="space-y-1">
                  <p className="flex items-center gap-2 text-sm text-gray-400">
                    <Info className="w-4 h-4" /> Worth a look before importing
                  </p>
                  <div className="max-h-32 overflow-y-auto text-xs text-gray-500 space-y-1 bg-gray-800/40 rounded-lg p-3">
                    {reviewRows.map(r => (
                      <p key={r.id}>{r.sheetName} row {r.sourceRow}: {r.reviewReason}</p>
                    ))}
                  </div>
                </div>
              )}

              {problemRows.length > 0 && (
                <div className="space-y-2">
                  <p className="text-sm text-red-400">
                    These cannot be imported as they stand. Skip them, or fix the file and start again.
                  </p>
                  <div className="max-h-40 overflow-y-auto space-y-1">
                    {problemRows.map(r => (
                      <div key={r.id} className="flex items-start gap-2 text-xs bg-red-600/5 rounded-lg px-3 py-2">
                        <span className="flex-1 text-red-400">
                          {r.sheetName} row {r.sourceRow}: {r.errorReason}
                        </span>
                        <button
                          onClick={() => changeResolution(r, 'skip')}
                          className="text-gray-500 hover:text-amber-500 whitespace-nowrap"
                        >
                          Skip
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {/* ── Footer ── */}
        <div className="flex items-center justify-between gap-2 px-5 py-4 border-t border-gray-800">
          <div>
            {step === 'review' && preview && problemRows.length > 0 && committed === null && (
              <button
                onClick={() => downloadErrors(preview.jobId)}
                className="text-xs text-gray-500 hover:text-amber-500"
              >
                Download problem rows
              </button>
            )}
          </div>

          <div className="flex items-center gap-2">
            {committed !== null ? (
              <button
                onClick={closeAll}
                className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors"
              >
                Close
              </button>
            ) : (
              <>
                {step !== 'upload' && (
                  <button
                    onClick={() => setStep(step === 'review' ? 'mapping' : 'sheets')}
                    disabled={busy || (step === 'mapping' && (preview?.sheets.length ?? 0) <= 1)}
                    className="flex items-center gap-1 px-3 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors disabled:opacity-40"
                  >
                    <ChevronLeft className="w-4 h-4" /> Back
                  </button>
                )}

                {step === 'upload' && (
                  <button
                    onClick={handleUpload}
                    disabled={!file || busy}
                    className="px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
                  >
                    {busy ? 'Reading…' : 'Read file'}
                  </button>
                )}

                {(step === 'sheets' || step === 'mapping') && (
                  <button
                    onClick={() => setStep(step === 'sheets' ? 'mapping' : 'review')}
                    disabled={busy}
                    className="px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
                  >
                    Next
                  </button>
                )}

                {step === 'review' && (
                  <button
                    onClick={handleCommit}
                    disabled={busy || (preview?.readyCount ?? 0) === 0}
                    className="px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
                  >
                    {busy ? 'Importing…' : `Import ${preview?.readyCount ?? 0}`}
                  </button>
                )}
              </>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone: string }) {
  return (
    <div className="bg-gray-800/40 rounded-lg px-4 py-3">
      <p className={`text-xl font-medium ${tone}`}>{value}</p>
      <p className="text-xs text-gray-500">{label}</p>
    </div>
  )
}

/**
 * Opens the problem-row report in a new tab rather than fetching it.
 *
 * The endpoint needs the same bearer token as everything else, so a plain link would 401;
 * this hands the browser a blob it already has, which also means the file is named the
 * way the server named it.
 */
async function downloadErrors(jobId: number) {
  const blob = await authenticatedBlob(importApi.errorReportUrl(jobId))
  downloadBlob(blob, `import-${jobId}-problems.csv`)
}
