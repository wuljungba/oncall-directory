export interface Department {
  id: number
  name: string
  description?: string
  azureAdGroupId?: string
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
  departmentId?: string
  department?: Department
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
  type: 'pto' | 'cme' | 'holiday' | 'sick'
  status: 'pending' | 'approved' | 'denied'
  notes?: string
}

export interface PhoneTree {
  id: number
  name: string
  treeType: 'emergency' | 'department' | 'oncall' | 'admin'
  departmentId?: number
  department?: Department
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
