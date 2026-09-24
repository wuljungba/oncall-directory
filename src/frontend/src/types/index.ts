// ── Tenant / Multi-Tenant Types ──

export interface Tenant {
  id: number
  name: string
  description?: string
  azureAdGroupId?: string
  /** Entra tenant GUID whose users may read this subscription (read-only). */
  azureAdTenantId?: string
  /** That directory's own name for itself, filled in when a customer connects. */
  directoryDisplayName?: string
  /** Its verified domains, as a JSON array — what makes an email address checkable. */
  directoryDomains?: string
  /** When a live Graph read last confirmed the directory can actually be read. */
  directoryVerifiedAt?: string
  contactEmail?: string
  /** Hospital | Clinic | PrivatePractice | SkilledNursing | EMS | Other. */
  organizationType?: string
  /** Unverified | Pending | Verified | Rejected. Only the healthcare types are gated. */
  verificationStatus?: string
  isActive: boolean
  createdAt: string
}

/**
 * A one-time invitation to connect a directory to a subscription.
 *
 * Two links, and both matter: the sign-in one lets the customer's staff sign in at all, and
 * the directory one lets OnCall read their directory. Neither names a directory — the
 * customer's admin says which by signing in — and both carry the same invite token.
 */
export interface OnboardingInvite {
  expiresAt: string
  signInConsentUrl: string
  directoryConsentUrl: string
  note: string
}

/**
 * Whether a subscription's directory is genuinely connected, asked live rather than inferred
 * from the presence of a GUID on the tenant row.
 */
export interface DirectoryStatus {
  directoryTenantId: string | null
  directoryDisplayName?: string
  directoryVerifiedAt?: string
  /** Consent exists, as far as Graph will say. */
  consentGranted: boolean
  /** The one that matters: the directory answers an app-only read right now. */
  canReadDirectory: boolean
  /** Readable only after someone consents again — a newer permission was added. */
  needsReconsent: boolean
  /** Why it cannot be read, when it cannot. */
  detail?: string | null
  lastSyncAt?: string | null
  lastOutcome?: string | null
  lastPagesRead?: number | null
  lastDeactivationsRefused?: number | null
  staffCount: number
}

export interface TenantAdmin {
  id: number
  tenantId: number
  tenant?: Tenant
  azureAdObjectId: string
  role: 'DepartmentAdmin' | 'SuperAdmin'
  isAutoAssigned: boolean
  createdAt: string
}

// ── Department ──

export interface Department {
  id: number
  name: string
  description?: string
  category?: string
  azureAdGroupId?: string
  tenantId?: number
  isActive: boolean
}

/**
 * "Person" is somebody who can be paged and may sign in. "Department" is a unit or
 * service line reached by phone -- "3North", x3434 -- with a displayName and a number,
 * and no name, email or sign-in identity.
 */
export type ContactType = 'Person' | 'Department'

/** See the backend's OrganizationVerification. None of this is PHI; it describes a business. */
export interface OrganizationVerification {
  id: number
  tenantId: number
  legalName: string
  doingBusinessAs?: string
  npi?: string
  addressLine1?: string
  city?: string
  state?: string
  postalCode?: string
  stateLicenseNumber?: string
  licenseState?: string
  ein?: string
  representativeName?: string
  representativeTitle?: string
  representativeEmail?: string
  submittedByName?: string
  submittedAt: string
  /** What the NPI and domain checks said, in a sentence an admin can read. */
  registryFindings?: string
  registryCheckedAt?: string
  decidedAt?: string
  decidedByName?: string
  decisionReason?: string
}

export interface Employee {
  id: string
  azureAdObjectId: string
  firstName: string
  lastName: string
  /** Present on a department contact, which has no first or last name. */
  displayName?: string
  contactType?: ContactType
  /** Post-nominal letters ("MD", "RN, BSN"), kept apart from the name. */
  credentials?: string
  title?: string
  specialty?: string
  clinicalRole?: string
  /** Optional: a department contact has no mailbox. */
  email?: string
  /** Internal extension, held apart from the dialable number. */
  extension?: string
  officePhone?: string
  mobilePhone?: string
  pagerNumber?: string
  officeLocation?: string
  departmentId?: number
  department?: Department
  tenantId?: number
  managerId?: string
  manager?: Employee
  certifications?: string[]
  languages?: string[]
  onCallStatus: boolean
  presence: 'available' | 'busy' | 'dnd' | 'offline' | 'unknown'
  isActive: boolean
  lastSyncedAt: string
  /**
   * How the record was first created: "Ad", "CsvImport", "Local", or "" on legacy rows.
   *
   * Deliberately a plain string, not a union — the empty value genuinely exists, and a union
   * would make the type claim something the data does not honour. Note this records how a
   * record was *first created*: a CSV row that merges into an existing person leaves it alone,
   * so it is not a record of which upload last touched someone.
   */
  source?: string
}

/** One record's outcome in a bulk admin action. */
export interface BulkItemResult {
  employeeId: string
  /** Null when the record was not found or belongs to another subscription. */
  displayName?: string
  email?: string
  outcome: string
  message?: string
  grantsRevoked: number
  /**
   * Local sign-in accounts switched off. Revoking grants alone leaves a working credential —
   * a LocalAccount has its own active flag and no foreign key to the employee.
   */
  signInsDisabled: number
  /** Holds admin rights that revoking permission grants does not remove. */
  isPrivilegedPrincipal: boolean
}

export interface BulkActionResult {
  action: string
  batchId: string
  requested: number
  succeeded: number
  blocked: number
  skipped: number
  notFound: number
  grantsRevoked: number
  signInsDisabled: number
  systemWideGrantsLeft: number
  privilegedPrincipals: number
  results: BulkItemResult[]
}

export interface BulkGrantItemResult {
  employeeId: string
  email?: string
  outcome: string
  message?: string
  grantId?: number
}

export interface BulkGrantResult {
  tenantId?: number
  permissions: string[]
  batchId: string
  requested: number
  granted: number
  replaced: number
  skipped: number
  notFound: number
  results: BulkGrantItemResult[]
}

export interface Schedule {
  id: number
  name: string
  departmentId: number
  department?: Department
  rotationType: 'weekly' | 'biweekly' | 'monthly'
  startDate: string
  endDate: string
  notes?: string
  isActive: boolean
  shifts?: Shift[]
  createdAt: string
}

export interface Shift {
  id: number
  scheduleId: number
  schedule?: Schedule
  employeeId: string
  employee?: Employee
  startTime: string
  endTime: string
  tier: 'primary' | 'secondary' | 'tertiary'
  status: 'scheduled' | 'swapped' | 'covered' | 'gap'
  notes?: string
}

export interface ShiftSwap {
  id: number
  originalShiftId: number
  originalShift?: Shift
  requestedById: string
  requestedBy?: Employee
  replacementUserId?: string
  replacementUser?: Employee
  status: 'pending' | 'approved' | 'rejected' | 'cancelled'
  reason?: string
  approvedById?: string
  approvedBy?: Employee
  approvedAt?: string
  createdAt: string
}

export interface TimeOff {
  id: number
  employeeId: string
  employee?: Employee
  startDate: string
  endDate: string
  type: 'pto' | 'cme' | 'holiday' | 'sick' | 'personal' | 'bereavement' | 'military' | 'jury_duty' | 'unpaid'
  status: 'pending' | 'approved' | 'denied'
  notes?: string
  approvedById?: string | null
  approvedBy?: Employee | null
  approvalReason?: string | null
  createdAt?: string
  updatedAt?: string
}

export interface PhoneTree {
  id: number
  name: string
  /**
   * Free-form, so a tenant can define codes that fit its own operation rather than only
   * the six that shipped. The server stores any string up to 20 characters. Common
   * values: emergency, department, oncall, admin, code-blue, code-red, code-green,
   * code-silver, code-grey, code-pink.
   */
  treeType: string
  departmentId?: number
  department?: Department
  procedure?: string
  fallbackProcedure?: string
  nodes: PhoneTreeNode[]
}

export interface PhoneTreeNode {
  id: number
  order: number
  employeeId?: string
  employee?: Employee
  roleName?: string
  condition?: string
  timeoutSeconds: number
}

export interface DutyHourRule {
  id: number
  name: string
  maxHoursPerPeriod: number
  periodDays: number
  minHoursBetweenShifts: number
  maxShiftLengthHours: number
  maxConsecutiveDays: number
  applicableRoles?: string
  departmentId?: number
  severity?: number
  isEnabled: boolean
}

export interface DutyHourViolation {
  id: number
  employeeId: string
  employee?: Employee
  ruleId: number
  rule?: DutyHourRule
  description: string
  severity: number
  isResolved: boolean
  violatedAt: string
}

export interface OnboardingIssue {
  id: string
  name: string
  email: string
  source: string
  isActive: boolean
  problems: string[]
}

export interface OnboardingHealth {
  total: number
  withIssues: number
  bySource: Record<string, number>
  issues: OnboardingIssue[]
}

export interface OnCallIncidentSummary {
  id: number
  startedAt: string
  endedAt?: string | null
  requestedByName?: string
  initiatedByName?: string
  notifiedByName?: string
  location?: string
  status?: string
  outcome?: string
}

export interface OnCallReportRow {
  employeeId: string
  employeeName: string
  tier: string
  start: string
  end: string
  status: string
  incidents: OnCallIncidentSummary[]
}

export interface AppSetting {
  key: string
  /** Null when the key is sensitive — the API withholds those values rather than listing them. */
  value: string | null
  description?: string
  updatedAt: string
  isSensitive?: boolean
}

export interface OnCallStatus {
  employeeId: string
  employeeName: string
  department: string
  tier: string
  startTime: string
  endTime: string
  role: string
}

export interface EscalationPolicy {
  id: number
  departmentId?: number
  department?: Department
  name: string
  maxResponseMinutes: number
  escalationTierCount: number
  notificationChannels: string
  isActive: boolean
  createdAt: string
}

export interface PhoneTreeEvent {
  id: number
  phoneTreeId: number
  startedAt: string
  endedAt?: string
  acknowledgedAt?: string
  initiatedById?: string
  initiatedBy?: Employee
  initiatedByName?: string
  /** Address of the account that raised it, captured from its token. */
  initiatedByEmail?: string
  requestedByName?: string
  notifiedByName?: string
  location?: string
  locationZone?: string
  externalIncidentId?: string
  responseTimeSeconds?: number
  status: 'active' | 'completed'
  outcome?: string
  notes?: string
  /** The single pre-debrief-log note, if this incident predates the log. Read-only. */
  debriefNotes?: string
  participants: PhoneTreeEventParticipant[]
  dispatchSteps?: DispatchStep[]
  debriefLog?: DebriefNote[]
  phoneTree?: PhoneTree
}

/** One append-only entry in an incident's debrief log. Never edited, never removed. */
export interface DebriefNote {
  id: number
  phoneTreeEventId: number
  note: string
  authorName?: string
  createdAt: string
}

export interface CodeCallLocation {
  id: number
  name: string
  zone?: string
  departmentId?: number
  department?: Department
  isActive: boolean
}

export interface PhoneTreeEventParticipant {
  id: number
  phoneTreeEventId: number
  employeeId?: string
  employee?: Employee
  role?: string
  respondedAt?: string
  acknowledgedAt?: string
  notes?: string
}

export interface DispatchStep {
  id: number
  phoneTreeEventId: number
  stepKey: string
  status: 'pending' | 'completed' | 'failed' | 'skipped'
  startedAt: string
  completedAt?: string
  detail?: string
  /** Provider's own message id (Twilio Message SID), used to settle delivery status. */
  providerMessageId?: string
}

/**
 * Someone who has actually signed in. Entra/Google tokens carry no app roles, so a new
 * user arrives with no access; this is how an admin finds them to grant it.
 */
export interface SignInIdentity {
  id: number
  provider: string
  /** Entra object id, or "google-{sub}" — the value needed to appoint a sub-admin. */
  externalObjectId: string
  email?: string
  displayName?: string
  firstSeenAt: string
  lastSeenAt: string
  isSuperAdmin: boolean
  tenantAdminOf: number[]
  permissions: string[]
  grantTenantIds: (number | null)[]
  /** Subscriptions this person is already a directory record in, regardless of access held. */
  homeTenantIds: number[]
  hasNoAccess: boolean
}

/** Health-check result for an external dispatch channel. */
export interface ConnectionStatus {
  connected: boolean
  detail: string
  lastCheckedAt: string
}

export interface EscalationEvent {
  id: number
  policyId: number
  policy?: EscalationPolicy
  employeeId: string
  employee?: Employee
  shiftId: number
  shift?: Shift
  tier: number
  status: 'pending' | 'resolved'
  triggeredAt: string
  resolvedAt?: string
  details: string
}

// ── Permission grants (admin → user) ──

export interface PermissionGrant {
  id: number
  tenantId?: number
  principalType: 'external' | 'local'
  externalPrincipalId: string
  permissions: string[]
  isActive: boolean
  createdAt: string
  updatedAt?: string
}

// ── Local accounts (admin managed) ──

export interface LocalAccount {
  id: number
  email: string
  displayName: string
  roles: string[]
  employeeId?: string | null
  isActive: boolean
  createdAt: string
  lastLoginAt?: string | null
}

// ── Public on-call permalink shares ──

export interface PublicShare {
  id: number
  tenantId: number
  tenant?: string
  token: string
  label: string
  isActive: boolean
  createdAt: string
  permalink: string
}

export interface PublicCoveredTier {
  covered: boolean
  assignments: number
}

export interface PublicCoveredUnit {
  departmentId: number
  department: string
  tiers: Record<string, PublicCoveredTier>
}

export interface PublicCoverage {
  coverageAt: string
  tenant: string
  units: PublicCoveredUnit[]
}

/** Someone asking to be let in from the public pages. Approving one triages it; it never grants access. */
export interface AccessRequest {
  id: number
  email: string
  fullName?: string
  organization?: string
  roleRequested?: string
  note?: string
  /**
   * The subscription this was attributed to, from the address's domain against a directory
   * OnCall has read for itself — never from the organization the person typed. Null means
   * nothing could attribute it, and only admins who see every subscription see those.
   */
  tenantId?: number | null
  /** Which verified domain matched, so the attribution can be seen rather than trusted. */
  matchedDomain?: string | null
  status: 'pending' | 'approved' | 'denied'
  createdAt: string
  reviewedAt?: string
  reviewedByName?: string
  reviewNote?: string
}
