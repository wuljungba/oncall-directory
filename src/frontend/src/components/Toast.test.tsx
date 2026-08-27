import { describe, it, expect, vi, afterEach } from 'vitest'
import { render, screen, act } from '@testing-library/react'
import { ToastProvider, useToast } from './Toast'

/** Renders a button that raises the given toast when clicked. */
function harness(toast: Parameters<ReturnType<typeof useToast>['addToast']>[0]) {
  function Raise() {
    const { addToast } = useToast()
    return <button onClick={() => addToast(toast)}>raise</button>
  }
  return render(
    <ToastProvider>
      <Raise />
    </ToastProvider>
  )
}

function raise() {
  act(() => {
    screen.getByRole('button', { name: 'raise' }).click()
  })
}

afterEach(() => {
  vi.useRealTimers()
})

describe('Toast', () => {
  it('renders the title and description', () => {
    harness({ type: 'success', title: 'Saved', description: 'Schedule updated' })
    raise()

    expect(screen.getByRole('alert')).toBeInTheDocument()
    expect(screen.getByText('Saved')).toBeInTheDocument()
    expect(screen.getByText('Schedule updated')).toBeInTheDocument()
  })

  it('styles a success toast differently from an error toast', () => {
    harness({ type: 'success', title: 'Saved' })
    raise()
    expect(screen.getByRole('alert').className).toContain('bg-green-600/10')
  })

  it('applies error styling', () => {
    harness({ type: 'error', title: 'Failed' })
    raise()
    expect(screen.getByRole('alert').className).toContain('bg-red-600/10')
  })

  it('auto-dismisses after the default duration', () => {
    vi.useFakeTimers()
    harness({ type: 'info', title: 'Heads up' })
    raise()
    expect(screen.getByRole('alert')).toBeInTheDocument()

    act(() => {
      vi.advanceTimersByTime(5000)
    })

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('dismisses when the close button is clicked', () => {
    harness({ type: 'info', title: 'Heads up' })
    raise()

    act(() => {
      screen.getByRole('button', { name: 'Dismiss' }).click()
    })

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
