import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'

const me = vi.hoisted(() => vi.fn())
const getAuthProvider = vi.hoisted(() => vi.fn())

vi.mock('@/services/api', () => ({ authApi: { me } }))
vi.mock('@/services/auth', () => ({
  getAuthProvider,
  getActiveProviderType: () => 'local',
}))

const { AuthProvider, useAuth } = await import('./useAuth')

const wrapper = ({ children }: { children: React.ReactNode }) => (
  <AuthProvider>{children}</AuthProvider>
)

/** A signed-in provider returning a fixed user. */
function signedIn() {
  return {
    init: vi.fn(() => Promise.resolve()),
    isAuthenticated: () => true,
    getCurrentUser: () => ({ id: 'user-1', name: 'Test User', email: 'test@example.com', provider: 'local', raw: {} }),
    getProviderType: () => 'local',
    getAccessToken: vi.fn(() => Promise.resolve('token-123')),
    refreshToken: vi.fn(() => Promise.resolve('token-123')),
    signIn: vi.fn(),
    signOut: vi.fn(),
  }
}

beforeEach(() => {
  sessionStorage.clear()
  me.mockReset()
  getAuthProvider.mockReset()
  getAuthProvider.mockReturnValue(signedIn())
})

describe('useAuth permission mapping', () => {
  it('derives granular flags from the permission list returned by the server', async () => {
    me.mockResolvedValue({ permissions: ['Schedule.Read', 'Schedule.Write', 'Directory.Read'], tenantIds: [7] })
    const { result } = renderHook(() => useAuth(), { wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.canScheduleWrite).toBe(true)
    expect(result.current.canDirectoryRead).toBe(true)
    expect(result.current.canAdminFull).toBe(false)
    expect(result.current.canAdminScoped).toBe(false)
    expect(result.current.canTenantManage).toBe(false)
  })

  // The client must never infer admin from anything but the server's own answer.
  it('grants no admin flags when the server returns an empty permission list', async () => {
    me.mockResolvedValue({ permissions: [], tenantIds: [] })
    const { result } = renderHook(() => useAuth(), { wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.permissions).toEqual([])
    expect(result.current.canAdminFull).toBe(false)
    expect(result.current.canAdminScoped).toBe(false)
    expect(result.current.canTenantManage).toBe(false)
    expect(result.current.canScheduleRead).toBe(false)
  })

  // An unreachable API is not a provisioning problem and must not be reported as one.
  it('distinguishes an unreachable API from a granted-nothing account', async () => {
    me.mockRejectedValue(new Error('network down'))
    const { result } = renderHook(() => useAuth(), { wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.permissionsUnavailable).toBe(true)
    expect(result.current.permissions).toEqual([])
  })

  it('auto-selects the only tenant for a scoped admin', async () => {
    me.mockResolvedValue({ permissions: ['Admin.Scoped'], tenantIds: [42] })
    const { result } = renderHook(() => useAuth(), { wrapper })

    await waitFor(() => expect(result.current.activeTenantId).toBe(42))
  })

  // A full admin sees every tenant, so picking one for them would silently narrow the view.
  it('does not auto-select a tenant for a full admin', async () => {
    me.mockResolvedValue({ permissions: ['Admin.Full', 'Admin.Scoped'], tenantIds: [42] })
    const { result } = renderHook(() => useAuth(), { wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(result.current.activeTenantId).toBeNull()
  })
})
