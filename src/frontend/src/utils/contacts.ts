import type { Employee } from '../types'

type Nameable = Pick<Employee, 'firstName' | 'lastName' | 'displayName'>

/**
 * What to call a contact in the UI.
 *
 * A department contact -- a unit or service line reached by phone, "3North" at x3434 --
 * carries its label in displayName and has no first or last name. Building a name from
 * those two produced an empty string: a blank row in the directory that could still be
 * clicked and opened.
 */
export function contactName(employee: Nameable): string {
  const person = `${employee.firstName ?? ''} ${employee.lastName ?? ''}`.trim()
  return person || employee.displayName?.trim() || 'Unnamed contact'
}

/** The initials shown in an avatar, for either kind of contact. */
export function contactInitials(employee: Nameable): string {
  const first = employee.firstName?.trim()
  const last = employee.lastName?.trim()
  if (first || last) return `${first?.charAt(0) ?? ''}${last?.charAt(0) ?? ''}`

  // A unit label is one word, so its first two characters read better than one initial.
  return (employee.displayName?.trim().slice(0, 2) ?? '?').toUpperCase()
}

/**
 * How to reach a contact by phone, preferring the number an outside caller can dial and
 * falling back to the internal extension.
 */
export function contactPhoneLabel(
  employee: Pick<Employee, 'officePhone' | 'mobilePhone' | 'extension'>,
): string | null {
  if (employee.officePhone) {
    return employee.extension ? `${employee.officePhone} ext. ${employee.extension}` : employee.officePhone
  }
  if (employee.mobilePhone) return employee.mobilePhone
  if (employee.extension) return `ext. ${employee.extension}`
  return null
}
