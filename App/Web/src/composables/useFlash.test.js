import { describe, it, expect, beforeEach } from 'vitest'
import { flash, flashNavigated, flashDismiss, useFlashState, savedMessage } from './useFlash'

// UI review theme 3: a message set before a navigation shows once, on the page it lands on.
describe('useFlash', () => {
  const state = useFlashState()
  beforeEach(() => {
    flashDismiss()
  })

  it('holds nothing at the start, and a navigation with nothing set does nothing', () => {
    flashNavigated()

    expect(state.message).toBeNull()
  })

  it('shows through the navigation that follows flash, and goes at the one after', () => {
    flash('Role saved.')

    flashNavigated() // the push that follows the save: the message is on the new page
    expect(state.message).toBe('Role saved.')

    flashNavigated() // the next navigation
    expect(state.message).toBeNull()
  })

  it('a message set again replaces the old one and shows for its own navigation', () => {
    flash('First.')
    flashNavigated()
    flash('Second.')

    flashNavigated()

    expect(state.message).toBe('Second.')
    flashNavigated()
    expect(state.message).toBeNull()
  })

  it('dismiss clears it at once', () => {
    flash('Saved.')
    flashNavigated()

    flashDismiss()

    expect(state.message).toBeNull()
    flashNavigated()
    expect(state.message).toBeNull()
  })

  it('the state a component reads cannot be written to', () => {
    flash('Saved.')
    state.message = 'Tampered'

    expect(state.message).toBe('Saved.')
  })
})

describe('savedMessage', () => {
  const t = (key, params) => (key === 'common.savedItem' ? `${params.name} saved.` : 'Saved.')

  it('names what was saved', () => {
    expect(savedMessage(t, 'Payroll')).toBe('Payroll saved.')
    expect(savedMessage(t, '  Payroll  ')).toBe('Payroll saved.')
    expect(savedMessage(t, 42)).toBe('42 saved.')
  })

  it('gives the plain line when there is no name', () => {
    expect(savedMessage(t, '')).toBe('Saved.')
    expect(savedMessage(t, '   ')).toBe('Saved.')
    expect(savedMessage(t, null)).toBe('Saved.')
    expect(savedMessage(t, undefined)).toBe('Saved.')
  })
})
