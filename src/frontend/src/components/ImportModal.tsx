import { useState, useRef } from 'react'
import { Upload, X, AlertTriangle, Check, FileText } from 'lucide-react'

interface ImportResult {
  totalRows: number
  imported: number
  errors: string[]
  isValid: boolean
}

interface ImportModalProps {
  isOpen: boolean
  onClose: () => void
  title: string
  description: string
  accept?: string
  onImport: (file: File) => Promise<ImportResult>
  onValidate?: (file: File) => Promise<ImportResult>
}

export default function ImportModal({
  isOpen,
  onClose,
  title,
  description,
  accept = '.csv',
  onImport,
  onValidate,
}: ImportModalProps) {
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [file, setFile] = useState<File | null>(null)
  const [importing, setImporting] = useState(false)
  const [validating, setValidating] = useState(false)
  const [result, setResult] = useState<ImportResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  if (!isOpen) return null

  async function handleValidate() {
    if (!file || !onValidate) return
    setValidating(true)
    setError(null)
    try {
      const res = await onValidate(file)
      setResult(res)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Validation failed')
    } finally {
      setValidating(false)
    }
  }

  async function handleImport() {
    if (!file) return
    setImporting(true)
    setError(null)
    try {
      const res = await onImport(file)
      setResult(res)
      if (res.isValid && res.errors.length === 0) {
        // Auto-close after brief delay on success
        setTimeout(() => {
          setFile(null)
          setResult(null)
          onClose()
        }, 2000)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Import failed')
    } finally {
      setImporting(false)
    }
  }

  function handleDrop(e: React.DragEvent) {
    e.preventDefault()
    const droppedFile = e.dataTransfer.files[0]
    if (droppedFile && droppedFile.name.endsWith('.csv')) {
      setFile(droppedFile)
      setResult(null)
      setError(null)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-gray-900 border border-gray-800 rounded-xl w-full max-w-lg mx-4"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-4 border-b border-gray-800">
          <h2 className="text-lg font-medium">{title}</h2>
          <button onClick={onClose} className="p-1 hover:bg-gray-800 rounded-lg transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Body */}
        <div className="p-5 space-y-4">
          <p className="text-sm text-gray-500">{description}</p>

          {/* Drop zone */}
          {!result && (
            <div
              onDrop={handleDrop}
              onDragOver={(e) => e.preventDefault()}
              onClick={() => fileInputRef.current?.click()}
              className={`border-2 border-dashed rounded-xl p-8 text-center cursor-pointer transition-colors ${
                file ? 'border-amber-600 bg-amber-600/5' : 'border-gray-700 hover:border-amber-600/50'
              }`}
            >
              <input
                ref={fileInputRef}
                type="file"
                accept={accept}
                className="hidden"
                onChange={(e) => {
                  const f = e.target.files?.[0]
                  if (f) {
                    setFile(f)
                    setResult(null)
                    setError(null)
                  }
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
                  <p className="text-sm text-gray-500">Drop a CSV file here, or click to browse</p>
                </>
              )}
            </div>
          )}

          {/* Error */}
          {error && (
            <div className="flex items-center gap-2 text-sm text-red-400 bg-red-600/10 rounded-lg px-4 py-3">
              <AlertTriangle className="w-4 h-4 flex-shrink-0" />
              {error}
            </div>
          )}

          {/* Result */}
          {result && (
            <div className="space-y-3">
              <div className={`flex items-center gap-2 text-sm rounded-lg px-4 py-3 ${
                result.isValid && result.errors.length === 0
                  ? 'bg-green-600/10 text-green-400'
                  : 'bg-yellow-600/10 text-yellow-400'
              }`}>
                {result.isValid && result.errors.length === 0 ? (
                  <Check className="w-4 h-4 flex-shrink-0" />
                ) : (
                  <AlertTriangle className="w-4 h-4 flex-shrink-0" />
                )}
                <span>
                  {result.totalRows} rows processed, {result.imported} imported
                  {result.errors.length > 0 && `, ${result.errors.length} errors`}
                </span>
              </div>
              {result.errors.length > 0 && (
                <div className="max-h-32 overflow-y-auto text-xs text-red-400 space-y-1 bg-red-600/5 rounded-lg p-3">
                  {result.errors.map((err, i) => (
                    <p key={i}>{err}</p>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-end gap-2 px-5 py-4 border-t border-gray-800">
          {onValidate && !result && file && (
            <button
              onClick={handleValidate}
              disabled={validating}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors disabled:opacity-50"
            >
              {validating ? 'Validating...' : 'Validate'}
            </button>
          )}
          {!result && file && (
            <button
              onClick={handleImport}
              disabled={importing}
              className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors disabled:opacity-50"
            >
              {importing ? (
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
              ) : (
                <Upload className="w-4 h-4" />
              )}
              {importing ? 'Importing...' : 'Import'}
            </button>
          )}
          {result && (
            <button
              onClick={() => {
                setFile(null)
                setResult(null)
                onClose()
              }}
              className="px-4 py-2 text-sm bg-gray-800 hover:bg-gray-700 rounded-lg transition-colors"
            >
              Close
            </button>
          )}
        </div>
      </div>
    </div>
  )
}
