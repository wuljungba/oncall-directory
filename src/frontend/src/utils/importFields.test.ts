import { describe, it, expect } from 'vitest'
import { IMPORT_FIELDS, TEMPLATE_COLUMNS, templateRows } from './importFields'

/**
 * The template and the import wizard's column dropdown both have to name fields the
 * backend parser recognises. They drifted once already — the template shipped
 * `departmentId` and no `department`, so the friendly column was invisible and the one it
 * offered needed a database lookup to fill in. These tests pin them together.
 *
 * The matching backend test (BulkImportTemplateTests) runs the same header list through
 * the real alias table, which is what stops this file agreeing with itself while
 * disagreeing with the importer.
 */
describe('directory import template', () => {
  it('offers every template column in the wizard dropdown', () => {
    const mappable = new Set(IMPORT_FIELDS.map(f => f.value))

    for (const column of TEMPLATE_COLUMNS) {
      expect(mappable, `template column '${column}' is not a mappable field`).toContain(column)
    }
  })

  it('includes department by name, not only by id', () => {
    // The whole point of the drift fix: an unrecognised department name fails the entire
    // import, and departmentId cannot be filled in without reading the database.
    expect(TEMPLATE_COLUMNS).toContain('department')
    expect(TEMPLATE_COLUMNS).toContain('departmentId')
  })

  it('carries the fields a code call depends on', () => {
    // mobilePhone is what CodeCallDispatchService dials. A template without it produces a
    // directory that looks populated and can page nobody.
    expect(TEMPLATE_COLUMNS).toContain('mobilePhone')
    expect(TEMPLATE_COLUMNS).toContain('email')
  })

  it('gives every sample row exactly one cell per column', () => {
    const [headers, ...samples] = templateRows()

    expect(headers).toEqual([...TEMPLATE_COLUMNS])
    expect(samples).toHaveLength(2)
    for (const row of samples) {
      expect(row).toHaveLength(TEMPLATE_COLUMNS.length)
    }
  })

  it('shows a person and a unit line, and labels which is which', () => {
    const [headers, person, unit] = templateRows()
    const cell = (row: string[], column: string) => row[headers.indexOf(column)]

    // A person: real name, a mailbox, no explicit displayName.
    expect(cell(person, 'contactType')).toBe('Person')
    expect(cell(person, 'email')).not.toBe('')
    expect(cell(person, 'firstName')).not.toBe('')

    // A unit: a label and a number, no name and no mailbox.
    expect(cell(unit, 'contactType')).toBe('Department')
    expect(cell(unit, 'email')).toBe('')
    expect(cell(unit, 'firstName')).toBe('')
    expect(cell(unit, 'displayName')).not.toBe('')
    expect(cell(unit, 'officePhone')).not.toBe('')
  })

  it('leaves the machine-facing columns blank in the samples', () => {
    const [headers, ...samples] = templateRows()

    // Both are optional and generated or resolved by the importer. A sample value would
    // read as "you need one of these", which is what pushed people toward departmentId.
    for (const row of samples) {
      expect(row[headers.indexOf('azureAdObjectId')]).toBe('')
      expect(row[headers.indexOf('departmentId')]).toBe('')
    }
  })
})
