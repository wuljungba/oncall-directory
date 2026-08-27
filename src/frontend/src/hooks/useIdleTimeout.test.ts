import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useIdleTimeout } from './useIdleTimeout'

beforeEach(() => vi.useFakeTimers())
afterEach(() => vi.useRealTimers())

/** Advances time inside act() so timer-driven state updates are flushed. */
function advance(ms: number) {
  act(() => { vi.advanceTimersByTime(ms) })
}

describe('useIdleTimeout', () => {
  it('signs the user out after the configured idle period', () => {
    const onTimeout = vi.fn()
    renderHook(() => useIdleTimeout(15, onTimeout))

    advance(14 * 60_000)
    expect(onTimeout).not.toHaveBeenCalled()

    advance(60_000)
    expect(onTimeout).toHaveBeenCalledTimes(1)
  })

  it('warns before signing out', () => {
    const { result } = renderHook(() => useIdleTimeout(15, vi.fn()))

    expect(result.current.isWarning).toBe(false)

    advance(14 * 60_000)
    expect(result.current.isWarning).toBe(true)
    expect(result.current.secondsRemaining).toBeGreaterThan(0)
  })

  it('restarts the countdown on user activity', () => {
    const onTimeout = vi.fn()
    renderHook(() => useIdleTimeout(15, onTimeout))

    advance(10 * 60_000)
    act(() => { window.dispatchEvent(new Event('keydown')) })

    // Without the reset this would have fired at the 15 minute mark.
    advance(10 * 60_000)
    expect(onTimeout).not.toHaveBeenCalled()

    advance(5 * 60_000)
    expect(onTimeout).toHaveBeenCalledTimes(1)
  })

  // Otherwise a stray scroll on an unattended workstation keeps the session alive, which
  // is exactly what auto-logoff exists to prevent.
  it('ignores activity once the warning is showing', () => {
    const onTimeout = vi.fn()
    const { result } = renderHook(() => useIdleTimeout(15, onTimeout))

    advance(14 * 60_000)
    expect(result.current.isWarning).toBe(true)

    act(() => { window.dispatchEvent(new Event('scroll')) })
    advance(60_000)

    expect(onTimeout).toHaveBeenCalledTimes(1)
  })

  it('stays signed in when the user says so explicitly', () => {
    const onTimeout = vi.fn()
    const { result } = renderHook(() => useIdleTimeout(15, onTimeout))

    advance(14 * 60_000)
    expect(result.current.isWarning).toBe(true)

    act(() => { result.current.staySignedIn() })
    expect(result.current.isWarning).toBe(false)

    advance(60_000)
    expect(onTimeout).not.toHaveBeenCalled()
  })

  it('is disabled when no timeout is configured', () => {
    const onTimeout = vi.fn()
    const { result } = renderHook(() => useIdleTimeout(0, onTimeout))

    advance(24 * 60 * 60_000)

    expect(onTimeout).not.toHaveBeenCalled()
    expect(result.current.isWarning).toBe(false)
  })
})
