import type {
  AppSetting,
  Department,
  DutyHourRule,
  DutyHourViolation,
  Employee,
  EscalationEvent,
  EscalationPolicy,
  Schedule,
  Shift,
  ShiftSwap,
  TimeOff,
  PhoneTree,
  PhoneTreeEvent,
  PhoneTreeEventParticipant,
  DispatchStep,
  CodeCallLocation,
  PhoneTreeNode,
  Tenant,
  TenantAdmin,
  PermissionGrant,
  LocalAccount,
  PublicShare,
  PublicCoverage,
  OnboardingHealth,
} from '@/types'
import { getAuthProvider } from '@/services/auth'

interface ImportResult {
  totalRows: number
  imported: number
  errors: string[]
  isValid: boolean
}

const API_BASE = '/api'

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true'

/**
 * Resolve the bearer token from the active auth provider.
 *
 * Delegates to the provider so tokens are silently refreshed when near
 * expiry. When the provider resolves null (e.g. expired local token with no
 * silent refresh) this returns null — the request goes out WITHOUT an
 * Authorization header rather than with a stale token. sessionStorage is only
 * used in dev mode or if the provider call itself throws.
 */
async function getAuthToken(): Promise<string | null> {
  if (DEV_AUTH) return sessionStorage.getItem('accessToken')

  try {
    return await getAuthProvider().getAccessToken()
  } catch (err) {
    console.warn('[api] Failed to retrieve access token, falling back to sessionStorage:', err)
    return sessionStorage.getItem('accessToken')
  }
}

/** Builds a query string from an object, omitting undefined and null values. */
function buildQueryString(params: Record<string, string | number | boolean | undefined | null>): string {
  const entries = Object.entries(params).filter(([, v]) => v != null && v !== undefined)
  if (entries.length === 0) return ''
  return '?' + entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join('&')
}

async function fetchApi<T>(
  endpoint: string,
  options?: RequestInit
): Promise<T> {
  const accessToken = await getAuthToken()
  const headers: HeadersInit = {
    'Content-Type': 'application/json',
    ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
    ...options?.headers,
  }

  const res = await fetch(`${API_BASE}${endpoint}`, { ...options, headers })

  if (!res.ok) {
    const error = await res.text()
    throw new Error(error || `API error: ${res.status}`)
  }

  return res.json()
}

// ── Departments ──
export const departmentsApi = {
  getAll: () => fetchApi<Department[]>('/departments'),
  get: (id: number) => fetchApi<Department>(`/departments/${id}`),
  create: (dept: Partial<Department>) =>
    fetchApi<Department>('/departments', {
      method: 'POST',
      body: JSON.stringify(dept),
    }),
}

// ── Schedule ──
export const scheduleApi = {
  getAll: (departmentId?: number) =>
    fetchApi<Schedule[]>(
      `/schedule${departmentId ? `?departmentId=${departmentId}` : ''}`
    ),
  get: (id: number) => fetchApi<Schedule>(`/schedule/${id}`),
  create: (schedule: Partial<Schedule>) =>
    fetchApi<Schedule>('/schedule', {
      method: 'POST',
      body: JSON.stringify(schedule),
    }),
  update: (id: number, schedule: Partial<Schedule>) =>
    fetchApi<Schedule>(`/schedule/${id}`, {
      method: 'PUT',
      body: JSON.stringify(schedule),
    }),
  delete: (id: number) =>
    fetchApi<void>(`/schedule/${id}`, { method: 'DELETE' }),
  generateShifts: (scheduleId: number, weeks: number = 4) =>
    fetchApi<Shift[]>(`/schedule/${scheduleId}/generate?weeks=${weeks}`, {
      method: 'POST',
    }),
  getShifts: (scheduleId: number, from?: string, to?: string) => {
    const params = new URLSearchParams()
    if (from) params.set('from', from)
    if (to) params.set('to', to)
    const qs = params.toString()
    return fetchApi<Shift[]>(
      `/schedule/${scheduleId}/shifts${qs ? `?${qs}` : ''}`
    )
  },
  assignShift: (scheduleId: number, shift: Partial<Shift>) =>
    fetchApi<Shift>(`/schedule/${scheduleId}/shifts`, {
      method: 'POST',
      body: JSON.stringify(shift),
    }),
  getOnCall: (departmentId?: number) =>
    fetchApi<Shift[]>(
      `/schedule/on-call${departmentId ? `?departmentId=${departmentId}` : ''}`
    ),
  requestSwap: (swap: Partial<ShiftSwap>) =>
    fetchApi<ShiftSwap>('/schedule/swaps', {
      method: 'POST',
      body: JSON.stringify(swap),
    }),
  approveSwap: (id: number) =>
    fetchApi<ShiftSwap>(`/schedule/swaps/${id}/approve`, { method: 'POST' }),
  getTimeOff: (employeeId: string) =>
    fetchApi<TimeOff[]>(`/schedule/time-off/${employeeId}`),
  getMyTimeOff: () =>
    fetchApi<TimeOff[]>('/schedule/time-off/me'),
  requestTimeOff: (timeOff: Partial<TimeOff>) =>
    fetchApi<TimeOff>('/schedule/time-off', {
      method: 'POST',
      body: JSON.stringify(timeOff),
    }),
  updateTimeOff: (id: number, timeOff: Partial<TimeOff>) =>
    fetchApi<TimeOff>(`/schedule/time-off/${id}`, {
      method: 'PUT',
      body: JSON.stringify(timeOff),
    }),
  cancelTimeOff: (id: number) =>
    fetchApi<void>(`/schedule/time-off/${id}`, { method: 'DELETE' }),
  approveTimeOff: (id: number, reason?: string) =>
    fetchApi<TimeOff>(`/schedule/time-off/${id}/approve`, {
      method: 'POST',
      body: JSON.stringify({ reason: reason ?? '' }),
    }),
  denyTimeOff: (id: number, reason?: string) =>
    fetchApi<TimeOff>(`/schedule/time-off/${id}/deny`, {
      method: 'POST',
      body: JSON.stringify({ reason: reason ?? '' }),
    }),
  getTimeOffReview: () =>
    fetchApi<TimeOff[]>('/schedule/time-off/review'),
  getAllTimeOff: (status?: string) =>
    fetchApi<TimeOff[]>(`/schedule/time-off/all${status ? `?status=${status}` : ''}`),
}

// ── Compliance ──
export const complianceApi = {
  getRules: (departmentId?: number) =>
    fetchApi<DutyHourRule[]>(`/compliance/rules${departmentId ? `?departmentId=${departmentId}` : ''}`),
  checkEmployee: (employeeId: string, from?: string, to?: string) => {
    const params = new URLSearchParams()
    if (from) params.set('from', from)
    if (to) params.set('to', to)
    const qs = params.toString()
    return fetchApi<DutyHourViolation[]>(`/compliance/check/${employeeId}${qs ? `?${qs}` : ''}`)
  },
  checkAll: (from?: string, to?: string) => {
    const params = new URLSearchParams()
    if (from) params.set('from', from)
    if (to) params.set('to', to)
    const qs = params.toString()
    return fetchApi<DutyHourViolation[]>(`/compliance/check${qs ? `?${qs}` : ''}`)
  },
  getHours: (employeeId: string, from: string, to: string) =>
    fetchApi<number>(`/compliance/hours/${employeeId}?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
}

// ── Bulk Import ──
async function uploadFile<T>(endpoint: string, file: File): Promise<T> {
  const accessToken = await getAuthToken()
  const formData = new FormData()
  formData.append('file', file)

  const headers: HeadersInit = {
    ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
  }

  const res = await fetch(`${API_BASE}${endpoint}`, {
    method: 'POST',
    headers,
    body: formData,
  })

  if (!res.ok) {
    const error = await res.text()
    throw new Error(error || `API error: ${res.status}`)
  }

  return res.json()
}

export const importApi = {
  validateEmployees: (file: File) => uploadFile<ImportResult>('/import/validate/employees', file),
  importEmployees: (file: File, tenantId?: number) =>
    uploadFile<ImportResult>(`/import/employees${tenantId ? `?tenantId=${tenantId}` : ''}`, file),
  validateShifts: (scheduleId: number, file: File) =>
    uploadFile<ImportResult>(`/import/validate/schedule/${scheduleId}`, file),
  importShifts: (scheduleId: number, file: File) =>
    uploadFile<ImportResult>(`/import/schedule/${scheduleId}`, file),
}

// ── Settings ──
export const settingsApi = {
  getAll: () => fetchApi<AppSetting[]>('/settings'),
  get: (key: string) => fetchApi<AppSetting>(`/settings/${encodeURIComponent(key)}`),
  upsert: (key: string, value: string, description?: string) =>
    fetchApi<AppSetting>(`/settings/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ value, description }),
    }),
}

// ── Integrations ──
export const integrationsApi = {
  syncAd: () =>
    fetchApi<{ synced: number }>('/integrations/sync/ad', { method: 'POST' }),
  sendTeamsNotification: (userId: string, title: string, message: string) =>
    fetchApi<{ sent: boolean }>('/integrations/notify/teams', {
      method: 'POST',
      body: JSON.stringify({ userId, title, message }),
    }),
  pushToCalendar: (userId: string, subject: string, startTime: string, endTime: string) =>
    fetchApi<{ pushed: boolean }>('/integrations/calendar/push', {
      method: 'POST',
      body: JSON.stringify({ userId, subject, startTime, endTime }),
    }),
  getPresence: (userId: string) =>
    fetchApi<{ userId: string; presence: string }>(`/integrations/presence/${userId}`),
}

// ── Directory ──
export const directoryApi = {
  search: (q: string, departmentId?: number) => {
    const params = new URLSearchParams({ q })
    if (departmentId) params.set('departmentId', String(departmentId))
    return fetchApi<Employee[]>(`/directory/search?${params}`)
  },
  getDepartment: (departmentId: number) =>
    fetchApi<Employee[]>(`/directory/department/${departmentId}`),
  get: (id: string) => fetchApi<Employee>(`/directory/${id}`),
  getByEmail: (email: string) =>
    fetchApi<Employee>(`/directory/by-email/${encodeURIComponent(email)}`),
  getPhoneTrees: (departmentId?: number) =>
    fetchApi<PhoneTree[]>(
      `/directory/phone-trees${departmentId ? `?departmentId=${departmentId}` : ''}`
    ),
  getPhoneTree: (id: number) =>
    fetchApi<PhoneTree>(`/directory/phone-trees/${id}`),
  getOnCall: (departmentId?: number) =>
    fetchApi<Employee[]>(
      `/directory/on-call${departmentId ? `?departmentId=${departmentId}` : ''}`
    ),
}

// ── Phone Trees (CRUD) ──
export const phoneTreesApi = {
  getAll: (departmentId?: number) =>
    fetchApi<PhoneTree[]>(`/phone-trees${departmentId ? `?departmentId=${departmentId}` : ''}`),
  get: (id: number) => fetchApi<PhoneTree>(`/phone-trees/${id}`),
  create: (tree: Partial<PhoneTree>) =>
    fetchApi<PhoneTree>('/phone-trees', { method: 'POST', body: JSON.stringify(tree) }),
  update: (id: number, tree: Partial<PhoneTree>) =>
    fetchApi<PhoneTree>(`/phone-trees/${id}`, { method: 'PUT', body: JSON.stringify(tree) }),
  delete: (id: number) => fetchApi<void>(`/phone-trees/${id}`, { method: 'DELETE' }),
  addNode: (treeId: number, node: Partial<PhoneTreeNode>) =>
    fetchApi<PhoneTreeNode>(`/phone-trees/${treeId}/nodes`, { method: 'POST', body: JSON.stringify(node) }),
  updateNode: (nodeId: number, node: Partial<PhoneTreeNode>) =>
    fetchApi<void>(`/phone-trees/nodes/${nodeId}`, { method: 'PUT', body: JSON.stringify(node) }),
  removeNode: (nodeId: number) => fetchApi<void>(`/phone-trees/nodes/${nodeId}`, { method: 'DELETE' }),
  reorder: (treeId: number, nodeIds: number[]) =>
    fetchApi<void>(`/phone-trees/${treeId}/reorder`, { method: 'POST', body: JSON.stringify(nodeIds) }),

  // Events (code call activations)
  getEvents: (treeId: number) =>
    fetchApi<PhoneTreeEvent[]>(`/phone-trees/${treeId}/events`),
  getEvent: (eventId: number) =>
    fetchApi<PhoneTreeEvent>(`/phone-trees/events/${eventId}`),
  createEvent: (treeId: number, evt: Partial<PhoneTreeEvent>) =>
    fetchApi<PhoneTreeEvent>(`/phone-trees/${treeId}/events`, { method: 'POST', body: JSON.stringify(evt) }),
  updateEvent: (eventId: number, evt: Partial<PhoneTreeEvent>) =>
    fetchApi<PhoneTreeEvent>(`/phone-trees/events/${eventId}`, { method: 'PUT', body: JSON.stringify(evt) }),
  deleteEvent: (eventId: number) =>
    fetchApi<void>(`/phone-trees/events/${eventId}`, { method: 'DELETE' }),
  addParticipant: (eventId: number, participant: Partial<PhoneTreeEventParticipant>) =>
    fetchApi<PhoneTreeEventParticipant>(`/phone-trees/events/${eventId}/participants`, { method: 'POST', body: JSON.stringify(participant) }),
  removeParticipant: (eventId: number, participantId: number) =>
    fetchApi<void>(`/phone-trees/events/${eventId}/participants/${participantId}`, { method: 'DELETE' }),
}

// ── Code Call Command Center ──
export const commandCenterApi = {
  getActive: () =>
    fetchApi<PhoneTreeEvent[]>('/phone-trees/events/active'),
  getResolved: () =>
    fetchApi<PhoneTreeEvent[]>('/phone-trees/events/resolved'),
  acknowledgeEvent: (eventId: number) =>
    fetchApi<PhoneTreeEvent>(`/phone-trees/events/${eventId}/acknowledge`, { method: 'POST' }),
  resolveEvent: (eventId: number, outcome?: string) =>
    fetchApi<PhoneTreeEvent>(`/phone-trees/events/${eventId}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ outcome }),
    }),
  addDispatchStep: (eventId: number, step: Partial<DispatchStep>) =>
    fetchApi<DispatchStep>(`/phone-trees/events/${eventId}/dispatch-step`, {
      method: 'POST',
      body: JSON.stringify(step),
    }),
  saveDebriefNotes: (eventId: number, notes: string) =>
    fetchApi<PhoneTreeEvent>(`/phone-trees/events/${eventId}/debrief`, {
      method: 'PUT',
      body: JSON.stringify({ notes }),
    }),
}

// ── Code Call Locations (for business owners) ──
export const codeCallLocationsApi = {
  getAll: (includeInactive = false) =>
    fetchApi<CodeCallLocation[]>(`/code-call-locations${includeInactive ? '?includeInactive=true' : ''}`),
  get: (id: number) =>
    fetchApi<CodeCallLocation>(`/code-call-locations/${id}`),
  create: (location: Partial<CodeCallLocation>) =>
    fetchApi<CodeCallLocation>('/code-call-locations', { method: 'POST', body: JSON.stringify(location) }),
  update: (id: number, location: Partial<CodeCallLocation>) =>
    fetchApi<CodeCallLocation>(`/code-call-locations/${id}`, { method: 'PUT', body: JSON.stringify(location) }),
  delete: (id: number) =>
    fetchApi<void>(`/code-call-locations/${id}`, { method: 'DELETE' }),
}

// ── Escalation ──
export const escalationApi = {
  getPolicies: (departmentId?: number) =>
    fetchApi<EscalationPolicy[]>(`/escalation/policies${departmentId ? `?departmentId=${departmentId}` : ''}`),
  createPolicy: (policy: Partial<EscalationPolicy>) =>
    fetchApi<EscalationPolicy>('/escalation/policies', { method: 'POST', body: JSON.stringify(policy) }),
  updatePolicy: (id: number, policy: Partial<EscalationPolicy>) =>
    fetchApi<EscalationPolicy>(`/escalation/policies/${id}`, { method: 'PUT', body: JSON.stringify(policy) }),
  deletePolicy: (id: number) =>
    fetchApi<void>(`/escalation/policies/${id}`, { method: 'DELETE' }),
  getEvents: (policyId?: number, limit?: number) => {
    const params = new URLSearchParams()
    if (policyId) params.set('policyId', String(policyId))
    if (limit) params.set('limit', String(limit))
    const qs = params.toString()
    return fetchApi<EscalationEvent[]>(`/escalation/events${qs ? `?${qs}` : ''}`)
  },
  acknowledgeEvent: (id: number) =>
    fetchApi<EscalationEvent>(`/escalation/events/${id}/acknowledge`, { method: 'POST' }),
}

// ── Auth (current user info + dev role-switching) ──
export const authApi = {
  me: () => fetchApi<CurrentUserResponse>('/auth/me'),
  devSetRole: (role: string) =>
    fetchApi<{ role: string; permissions: string[]; message: string }>(
      `/auth/dev/set-role?role=${role}`,
      { method: 'POST' }
    ),
  devClearRole: () =>
    fetchApi<{ role: string; permissions: string[]; message: string }>(
      '/auth/dev/clear-role',
      { method: 'POST' }
    ),
}

export interface CurrentUserResponse {
  id: string
  name: string
  email: string
  roles: string[]
  permissions: string[]
  tenantIds: number[]
  tenantRoles: Record<string, string>
  employeeId?: string | null
}

// ── Tenant Management ──
export const tenantsApi = {
  getAll: (includeInactive = false) =>
    fetchApi<Tenant[]>(`/tenants${includeInactive ? '?includeInactive=true' : ''}`),
  get: (id: number) =>
    fetchApi<Tenant>(`/tenants/${id}`),
  create: (data: Record<string, unknown>) =>
    fetchApi<Tenant>('/tenants', { method: 'POST', body: JSON.stringify(data) }),
  update: (id: number, data: Record<string, unknown>) =>
    fetchApi<Tenant>(`/tenants/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  deactivate: (id: number) =>
    fetchApi<void>(`/tenants/${id}`, { method: 'DELETE' }),
  getAdmins: (tenantId: number) =>
    fetchApi<TenantAdmin[]>(`/tenants/${tenantId}/admins`),
  assignAdmin: (tenantId: number, data: Record<string, unknown>) =>
    fetchApi<TenantAdmin>(`/tenants/${tenantId}/admins`, { method: 'POST', body: JSON.stringify(data) }),
  updateAdmin: (tenantId: number, adminId: number, data: Record<string, unknown>) =>
    fetchApi<TenantAdmin>(`/tenants/${tenantId}/admins/${adminId}`, { method: 'PUT', body: JSON.stringify(data) }),
  removeAdmin: (tenantId: number, adminId: number) =>
    fetchApi<void>(`/tenants/${tenantId}/admins/${adminId}`, { method: 'DELETE' }),
}

// ── Admin (account & department management) ──
export const adminApi = {
  // Employees
  getAllEmployees: (includeInactive = false, tenantId?: number) =>
    fetchApi<Employee[]>(`/admin/employees${buildQueryString({ includeInactive: includeInactive || undefined, tenantId })}`),
  getEmployee: (id: string) =>
    fetchApi<Employee>(`/admin/employees/${id}`),
  createEmployee: (data: Record<string, unknown>) =>
    fetchApi<Employee>('/admin/employees', { method: 'POST', body: JSON.stringify(data) }),
  updateEmployee: (id: string, data: Record<string, unknown>) =>
    fetchApi<Employee>(`/admin/employees/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  deactivateEmployee: (id: string) =>
    fetchApi<void>(`/admin/employees/${id}`, { method: 'DELETE' }),
  reactivateEmployee: (id: string) =>
    fetchApi<void>(`/admin/employees/${id}/reactivate`, { method: 'POST' }),
  hardDeleteEmployee: (id: string) =>
    fetchApi<void>(`/admin/employees/${id}/hard-delete`, { method: 'DELETE' }),
  getDirectReports: (id: string) =>
    fetchApi<Employee[]>(`/admin/employees/${id}/direct-reports`),

  // Departments
  getAllDepartments: (includeInactive = false, tenantId?: number) =>
    fetchApi<Department[]>(`/admin/departments${buildQueryString({ includeInactive: includeInactive || undefined, tenantId })}`),
  createDepartment: (data: Record<string, unknown>) =>
    fetchApi<Department>('/admin/departments', { method: 'POST', body: JSON.stringify(data) }),
  updateDepartment: (id: number, data: Record<string, unknown>) =>
    fetchApi<Department>(`/admin/departments/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  deactivateDepartment: (id: number) =>
    fetchApi<void>(`/admin/departments/${id}`, { method: 'DELETE' }),
  getDepartmentMembers: (id: number) =>
    fetchApi<Employee[]>(`/admin/departments/${id}/members`),
}

// ── Admin: per-user permission grants (on-call schedule read/write) ──
export const permissionsAdminApi = {
  list: (tenantId?: number) =>
    fetchApi<PermissionGrant[]>(`/admin/permissions${tenantId ? `?tenantId=${tenantId}` : ''}`),
  create: (data: { tenantId?: number; principalType?: string; externalPrincipalId: string; permissions: string }) =>
    fetchApi<PermissionGrant>('/admin/permissions', {
      method: 'POST',
      body: JSON.stringify(data),
    }),
  update: (id: number, data: { externalPrincipalId?: string; principalType?: string; permissions?: string; isActive?: boolean }) =>
    fetchApi<PermissionGrant>(`/admin/permissions/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),
  remove: (id: number) =>
    fetchApi<void>(`/admin/permissions/${id}`, { method: 'DELETE' }),
}

// ── Admin: onboarding standard health ──
export const onboardingApi = {
  getHealth: () => fetchApi<OnboardingHealth>('/admin/onboarding/health'),
}

// ── Admin: public permalink shares (coverage-only on-call schedule) ──
export const sharesApi = {
  list: () => fetchApi<PublicShare[]>('/admin/shares'),
  create: (tenantId: number, label?: string) =>
    fetchApi<PublicShare>('/admin/shares', {
      method: 'POST',
      body: JSON.stringify({ tenantId, label }),
    }),
  setActive: (id: number, isActive: boolean) =>
    fetchApi<PublicShare>(`/admin/shares/${id}/active`, {
      method: 'PUT',
      body: JSON.stringify({ isActive }),
    }),
  remove: (id: number) =>
    fetchApi<void>(`/admin/shares/${id}`, { method: 'DELETE' }),
}

// ── Admin: local accounts (email+password users) ──
export const localAccountsApi = {
  list: (includeInactive = false) =>
    fetchApi<LocalAccount[]>(`/auth/local${includeInactive ? '?includeInactive=true' : ''}`),
  create: (data: { email: string; password: string; displayName?: string; roles?: string[]; employeeId?: string | null }) =>
    fetchApi<LocalAccount>('/auth/local/register', {
      method: 'POST',
      body: JSON.stringify(data),
    }),
  update: (id: number, data: { displayName?: string; isActive?: boolean; roles?: string[]; employeeId?: string | null }) =>
    fetchApi<LocalAccount>(`/auth/local/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),
  resetPassword: (id: number, newPassword: string) =>
    fetchApi<void>(`/auth/local/${id}/reset-password`, {
      method: 'POST',
      body: JSON.stringify({ newPassword }),
    }),
  remove: (id: number) =>
    fetchApi<void>(`/auth/local/${id}`, { method: 'DELETE' }),
}

// ── Public on-call coverage (perm-ink; unauthenticated) ──
export const publicApi = {
  getOnCallCoverage: (token: string) =>
    fetchApi<PublicCoverage>(`/public/schedule/on-call/${encodeURIComponent(token)}`),
}
