import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter, Routes, Route } from 'react-router-dom'

const useAuth = vi.hoisted(() => vi.fn())
vi.mock('@/hooks/useAuth', () => ({ useAuth }))

// Imported after the mock is registered so the guards pick it up.
const { AdminRoute, ProtectedRoute } = await import('@/App')

/** Minimal auth state; each test overrides only the fields it cares about. */
function auth(over: Record<string, unknown> = {}) {
  return { isLoading: false, isAuthenticated: true, isAdmin: false, canAdminScoped: false, ...over }
}

function renderGuard(guard: React.ReactNode) {
  return render(
    <MemoryRouter initialEntries={['/admin']}>
      <Routes>
        <Route path="/admin" element={guard} />
        <Route path="/dashboard" element={<div>dashboard</div>} />
        <Route path="/login" element={<div>login</div>} />
      </Routes>
    </MemoryRouter>
  )
}

beforeEach(() => useAuth.mockReset())

describe('AdminRoute', () => {
  it('renders admin content for a full admin', () => {
    useAuth.mockReturnValue(auth({ isAdmin: true }))
    renderGuard(<AdminRoute><div>admin content</div></AdminRoute>)
    expect(screen.getByText('admin content')).toBeInTheDocument()
  })

  it('renders admin content for a tenant-scoped admin', () => {
    useAuth.mockReturnValue(auth({ canAdminScoped: true }))
    renderGuard(<AdminRoute><div>admin content</div></AdminRoute>)
    expect(screen.getByText('admin content')).toBeInTheDocument()
  })

  it('redirects a non-admin to the dashboard', () => {
    useAuth.mockReturnValue(auth())
    renderGuard(<AdminRoute><div>admin content</div></AdminRoute>)
    expect(screen.queryByText('admin content')).not.toBeInTheDocument()
    expect(screen.getByText('dashboard')).toBeInTheDocument()
  })

  // Regression: the guard used to decide before permissions had loaded, bouncing a real
  // admin to /dashboard on every refresh. While loading it must decide nothing.
  it('waits instead of redirecting while permissions are still loading', () => {
    useAuth.mockReturnValue(auth({ isLoading: true }))
    const { container } = renderGuard(<AdminRoute><div>admin content</div></AdminRoute>)
    expect(screen.queryByText('dashboard')).not.toBeInTheDocument()
    expect(screen.queryByText('admin content')).not.toBeInTheDocument()
    expect(container.querySelector('.animate-spin')).toBeInTheDocument()
  })
})

describe('ProtectedRoute', () => {
  it('renders content for an authenticated user', () => {
    useAuth.mockReturnValue(auth())
    renderGuard(<ProtectedRoute><div>protected</div></ProtectedRoute>)
    expect(screen.getByText('protected')).toBeInTheDocument()
  })

  it('redirects an unauthenticated user to login', () => {
    useAuth.mockReturnValue(auth({ isAuthenticated: false }))
    renderGuard(<ProtectedRoute><div>protected</div></ProtectedRoute>)
    expect(screen.getByText('login')).toBeInTheDocument()
  })

  it('waits instead of redirecting while auth is still loading', () => {
    useAuth.mockReturnValue(auth({ isLoading: true, isAuthenticated: false }))
    renderGuard(<ProtectedRoute><div>protected</div></ProtectedRoute>)
    expect(screen.queryByText('login')).not.toBeInTheDocument()
  })
})
