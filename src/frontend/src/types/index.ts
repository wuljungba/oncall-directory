// ── Tenant / Multi-Tenant Types ──

export interface Tenant {
  id: number
  name: string
  description?: string
  azureAdGroupId?: string
  contactEmail?: string
  isActive: boolean
  createdAt: string
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

export interface Employee {
  id: string
  azureAdObjectId: string
  firstName: string
  lastName: string
  title?: string
  specialty?: string
  clinicalRole?: string
  email: string
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
  value: string
  description?: string
  updatedAt: string
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
