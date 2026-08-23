import { describe, it, expect, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import Toast from './Toast'

describe('Toast', () => {
  it('should render toast message', () => {
    render(
      <Toast
        message="Test message"
        type="success"
        onClose={() => {}}
      />
    )

    expect(screen.getByText('Test message')).toBeInTheDocument()
  })

  it('should apply success styling', () => {
    const { container } = render(
      <Toast
        message="Success"
        type="success"
        onClose={() => {}}
      />
    )

    const toast = container.firstChild
    expect(toast).toHaveClass('bg-green-600/10')
  })

  it('should apply error styling', () => {
    const { container } = render(
      <Toast
        message="Error"
        type="error"
        onClose={() => {}}
      />
    )

    const toast = container.firstChild
    expect(toast).toHaveClass('bg-red-600/10')
  })

  it('should auto-dismiss after 5 seconds', async () => {
    const onClose = vi.fn()
    const { unmount } = render(
      <Toast
        message="Test"
        type="info"
        onClose={onClose}
      />
    )

    await waitFor(
      () => expect(onClose).toHaveBeenCalled(),
      { timeout: 6000 }
    )

    unmount()
  })

  it('should dismiss on close button click', async () => {
    const onClose = vi.fn()
    render(
      <Toast
        message="Test"
        type="info"
        onClose={onClose}
      />
    )

    const closeButton = screen.getByRole('button')
    closeButton.click()

    expect(onClose).toHaveBeenCalled()
  })
})
