/**
 * The one place that knows what columns the directory importer understands.
 *
 * Two things have to agree with the backend's canonical field names
 * (`BulkImportService.HeaderAliases`): the column dropdown in the import wizard, and the
 * template we hand people to fill in. They used to live in separate files and drifted —
 * the template offered `departmentId`, an internal integer nobody can supply without
 * reading the database, and omitted `department`, which takes the name and is what the
 * parser resolves. Keeping both here means a new field is added once.
 */

/** A field a spreadsheet column can be mapped to. `''` means "ignore this column". */
export interface ImportField {
  value: string
  label: string
}

export const IMPORT_FIELDS: ImportField[] = [
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

/**
 * The template's columns, in the order someone filling it in wants to meet them: who the
 * person is, how to reach them, where they belong, then the two machine-facing columns
 * nobody types by hand.
 *
 * `azureAdObjectId` used to come first, which made an Entra GUID the very first thing in
 * the file. It is optional — the importer generates one — so it belongs at the end.
 */
export const TEMPLATE_COLUMNS = [
  'firstName', 'lastName', 'displayName', 'email', 'title', 'credentials',
  'officePhone', 'mobilePhone', 'extension', 'officeLocation',
  'department', 'departmentId', 'contactType', 'azureAdObjectId',
] as const

/** A clinician: a real name and a mailbox. */
const PERSON_ROW = [
  'Jane', 'Smith', '', 'jane.smith@hospital.org', 'Attending Physician', 'MD',
  '+12025551234', '+12025555678', '', 'Floor 3 - West Wing',
  'Cardiology', '', 'Person', '',
]

/**
 * A unit or service line: a label and a number, no name and no mailbox. `contactType` is
 * set explicitly here — the importer can infer it, but then flags the row for review, and
 * showing the column is how someone learns it exists.
 */
const UNIT_ROW = [
  '', '', '3North', '', '', '',
  '845-568-3434', '', '3434', 'Floor 3 - North Wing',
  'Cardiology', '', 'Department', '',
]

export const TEMPLATE_FILENAME = 'directory-import-template.csv'

/** Header row plus the two worked examples, ready for `downloadCsv`. */
export function templateRows(): string[][] {
  return [[...TEMPLATE_COLUMNS], PERSON_ROW, UNIT_ROW]
}
