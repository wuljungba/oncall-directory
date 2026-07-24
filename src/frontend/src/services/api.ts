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
  PhoneTreeNode,
} from '@/types'

interface ImportResult {
  totalRows: number
  imported: number
  errors: string[]
  isValid: boolean
}

const API_BASE = '/api'

async function fetchApi<T>(
  endpoint: string,
  options?: RequestInit
): Promise<T> {
  const accessToken = sessionStorage.getItem('accessToken')
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
  const accessToken = sessionStorage.getItem('accessToken')
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
  importEmployees: (file: File) => uploadFile<ImportResult>('/import/employees', file),
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
}

// ── Admin (account & department management) ──
export const adminApi = {
  // Employees
  getAllEmployees: (includeInactive = false) =>
    fetchApi<Employee[]>(`/admin/employees${includeInactive ? '?includeInactive=true' : ''}`),
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
  getDirectReports: (id: string) =>
    fetchApi<Employee[]>(`/admin/employees/${id}/direct-reports`),

  // Departments
  getAllDepartments: (includeInactive = false) =>
    fetchApi<Department[]>(`/admin/departments${includeInactive ? '?includeInactive=true' : ''}`),
  createDepartment: (data: Record<string, unknown>) =>
    fetchApi<Department>('/admin/departments', { method: 'POST', body: JSON.stringify(data) }),
  updateDepartment: (id: number, data: Record<string, unknown>) =>
    fetchApi<Department>(`/admin/departments/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
  deactivateDepartment: (id: number) =>
    fetchApi<void>(`/admin/departments/${id}`, { method: 'DELETE' }),
  getDepartmentMembers: (id: number) =>
    fetchApi<Employee[]>(`/admin/departments/${id}/members`),
}
