/**
 * E.164 phone number validation utilities.
 *
 * E.164 format: +{country code}{national number}
 *   - Starts with '+'
 *   - Country code is 1-3 digits (no leading zero)
 *   - National number is up to 14 digits
 *   - Total max 15 digits including country code
 *
 * Examples: +12025551234, +447911123456, +81312345678
 */

const E164_REGEX = /^\+[1-9]\d{1,14}$/

/**
 * Check if a phone number is a valid E.164 format.
 * Returns true for valid E.164, false otherwise.
 * Empty/null/undefined returns true (field is optional).
 */
export function isValidE164(phone: string | null | undefined): boolean {
  if (!phone) return true // optional field
  return E164_REGEX.test(phone)
}

/**
 * Validate a phone number and return an error message if invalid.
 * Returns null if valid or empty.
 */
export function validateE164(
  phone: string | null | undefined,
  label: string = 'Phone number',
): string | null {
  if (!phone) return null // optional
  if (!isValidE164(phone)) {
    return `${label} must be in E.164 format (e.g. +12025551234) — starts with '+', then country code + number, no spaces or special chars.`
  }
  return null
}

/**
 * Strip all non-digit, non-'+' characters from a phone string.
 * Useful for cleaning user input before validation.
 */
export function sanitizePhone(raw: string): string {
  return raw.replace(/[^\d+]/g, '')
}

/**
 * Format a phone number for display (truncates middle digits).
 * Does NOT validate — use isValidE164 first.
 */
export function formatPhoneForDisplay(phone: string): string {
  if (phone.length <= 8) return phone
  // Show first 4 and last 4: +1202...1234
  return `${phone.slice(0, 5)}...${phone.slice(-4)}`
}
