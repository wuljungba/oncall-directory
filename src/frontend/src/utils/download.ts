/**
 * Browser file downloads.
 *
 * This was copy-pasted in four places, each with the same two latent bugs: the anchor was
 * never added to the document (which some browsers refuse to activate), and the object URL
 * was revoked synchronously after click(), which can cancel the download before it starts.
 */

/** Triggers a download of the given blob. */
export function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = filename
  anchor.style.display = 'none'
  document.body.appendChild(anchor)
  anchor.click()
  document.body.removeChild(anchor)
  // Revoked on the next tick: doing it synchronously can cancel the download in progress.
  setTimeout(() => URL.revokeObjectURL(url), 0)
}

/**
 * Escapes one CSV field per RFC 4180.
 *
 * Fields containing a quote must double it. Wrapping in quotes without escaping — as the
 * compliance export did — produces a file that breaks at the first quoted value.
 */
export function escapeCsvField(value: unknown): string {
  const text = value == null ? '' : String(value)
  return /[",\n\r]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

/** Builds RFC 4180 CSV text from a row matrix. The first row is treated as the header. */
export function toCsv(rows: readonly (readonly unknown[])[]): string {
  return rows.map(row => row.map(escapeCsvField).join(',')).join('\r\n')
}

/**
 * Downloads a row matrix as CSV.
 *
 * Prefixed with a UTF-8 BOM so Excel reads accented names correctly instead of mojibake —
 * these are people's names, and getting them wrong is not a cosmetic problem.
 */
export function downloadCsv(filename: string, rows: readonly (readonly unknown[])[]): void {
  const blob = new Blob(['﻿', toCsv(rows)], { type: 'text/csv;charset=utf-8;' })
  downloadBlob(blob, filename)
}
