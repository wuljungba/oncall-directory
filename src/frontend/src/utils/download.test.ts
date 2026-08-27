import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { downloadBlob, downloadCsv, escapeCsvField, toCsv } from './download'

describe('escapeCsvField', () => {
  it('leaves plain values alone', () => {
    expect(escapeCsvField('Jane')).toBe('Jane')
  })

  it('quotes values containing a comma or newline', () => {
    expect(escapeCsvField('Floor 3, West Wing')).toBe('"Floor 3, West Wing"')
    expect(escapeCsvField('a\nb')).toBe('"a\nb"')
  })

  // The old compliance export wrapped fields in quotes without doubling inner quotes,
  // so one quoted value corrupted the rest of the file.
  it('doubles embedded quotes', () => {
    expect(escapeCsvField('Jane "JJ" Smith')).toBe('"Jane ""JJ"" Smith"')
  })

  it('renders null and undefined as empty, never as a sentinel', () => {
    expect(escapeCsvField(null)).toBe('')
    expect(escapeCsvField(undefined)).toBe('')
  })
})

describe('toCsv', () => {
  it('joins rows with CRLF', () => {
    expect(toCsv([['a', 'b'], ['c', 'd']])).toBe('a,b\r\nc,d')
  })
})

describe('downloadBlob', () => {
  let createObjectURL: ReturnType<typeof vi.fn>
  let revokeObjectURL: ReturnType<typeof vi.fn>

  beforeEach(() => {
    vi.useFakeTimers()
    createObjectURL = vi.fn(() => 'blob:fake')
    revokeObjectURL = vi.fn()
    Object.assign(URL, { createObjectURL, revokeObjectURL })
  })

  afterEach(() => vi.useRealTimers())

  it('activates an anchor that is attached to the document, then cleans up', () => {
    const click = vi.fn(function (this: HTMLAnchorElement) {
      // The anchor must be in the DOM at the moment it is activated.
      expect(document.body.contains(this)).toBe(true)
    })
    const original = HTMLAnchorElement.prototype.click
    HTMLAnchorElement.prototype.click = click

    try {
      downloadBlob(new Blob(['x']), 'contacts.csv')
    } finally {
      HTMLAnchorElement.prototype.click = original
    }

    expect(click).toHaveBeenCalledOnce()
    expect(document.querySelector('a[download]')).toBeNull()
    // Not revoked synchronously — that can cancel the download.
    expect(revokeObjectURL).not.toHaveBeenCalled()
    vi.runAllTimers()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:fake')
  })

  it('names the file', () => {
    let seen = ''
    const original = HTMLAnchorElement.prototype.click
    HTMLAnchorElement.prototype.click = function (this: HTMLAnchorElement) { seen = this.download }
    try {
      downloadCsv('clean-contacts.csv', [['First Name'], ['Jane']])
    } finally {
      HTMLAnchorElement.prototype.click = original
    }
    expect(seen).toBe('clean-contacts.csv')
  })
})
