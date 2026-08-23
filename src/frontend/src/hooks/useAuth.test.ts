import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { useAuth } from './useAuth'

// Mock the API and auth services
vi.mock('@/services/api', () => ({
  adminApi: {
    getPermissions: vi.fn(() => Promise.resolve({
      canScheduleWrite: true,
      canAdminFull: false,
      canAdminScoped: false,
      canTenantManage: false,
    })),
  },
}))

vi.mock('@/services/auth/authFactory', () => ({
  getAuthProvider: vi.fn(() => ({
    isAuthenticated: () => true,
    getUser: () => ({ id: 'user-1', name: 'Test User', mail: 'test@example.com' }),
    getAccessToken: vi.fn(() => Promise.resolve('token-123')),
    logout: vi.fn(),
  })),
}))

describe('useAuth', () => {
  beforeEach(() => {
    sessionStorage.clear()
  })

  it('should return authentication status', async () => {
    const { result } = renderHook(() => useAuth())

    await waitFor(() => {
      expect(result.current.user).toBeDefined()
    })
  })

  it('should track active tenant ID', async () => {
    sessionStorage.setItem('activeTenantId', '42')
    const { result } = renderHook(() => useAuth())

    await waitFor(() => {
      expect(result.current.activeTenantId).toBe(42)
    })
  })

  it('should parse permissions from response', async () => {
    const { result } = renderHook(() => useAuth())

    await waitFor(() => {
      expect(result.current.canScheduleWrite).toBe(true)
    })
  })
})
