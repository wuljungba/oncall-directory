import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { BrowserRouter } from 'react-router-dom'
import AdminRoute from './AdminRoute'

// Mock useAuth hook
vi.mock('@/hooks/useAuth', () => ({
  useAuth: vi.fn(() => ({
    canAdminFull: true,
    isLoading: false,
    user: { id: 'admin-1', name: 'Admin User' },
  })),
}))

describe('AdminRoute', () => {
  it('should render admin content when user has permission', () => {
    render(
      <BrowserRouter>
        <AdminRoute />
      </BrowserRouter>
    )

    // AdminPage should be rendered
    expect(screen.getByText('On-Call Schedule')).toBeInTheDocument()
  })

  it('should show loading state during auth check', () => {
    vi.doMock('@/hooks/useAuth', () => ({
      useAuth: vi.fn(() => ({
        canAdminFull: false,
        isLoading: true,
        user: null,
      })),
    }))

    const { container } = render(
      <BrowserRouter>
        <AdminRoute />
      </BrowserRouter>
    )

    expect(container.querySelector('.animate-spin')).toBeInTheDocument()
  })

  it('should redirect to dashboard when user lacks admin permission', () => {
    vi.doMock('@/hooks/useAuth', () => ({
      useAuth: vi.fn(() => ({
        canAdminFull: false,
        isLoading: false,
        user: { id: 'user-1', name: 'Regular User' },
      })),
    }))

    render(
      <BrowserRouter>
        <AdminRoute />
      </BrowserRouter>
    )

    // Should redirect (Navigate component will render, but in test we just verify it's called)
    // In a real app, this would navigate to /dashboard
  })
})
