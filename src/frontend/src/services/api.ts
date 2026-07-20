import type {
  AppSetting,
  Department,
  Employee,
  Schedule,
  Shift,
  ShiftSwap,
  TimeOff,
  PhoneTree,
} from '@/types'

interface ImportResult {
  totalRows: number
  imported: number
  errors: string[]
  isVAlid: boolean
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
