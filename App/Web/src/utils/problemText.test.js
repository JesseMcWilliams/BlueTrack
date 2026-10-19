import { describe, it, expect } from 'vitest'
import { problemReason, problemMessage } from './problemText'

// UI review theme 1: the reason a refused request gives.
describe('problemReason', () => {
  it('prefers detail, then message, then error', () => {
    expect(problemReason({ detail: 'D', message: 'M', error: 'E' })).toBe('D')
    expect(problemReason({ message: 'M', error: 'E' })).toBe('M')
    expect(problemReason({ error: 'E' })).toBe('E')
  })

  it('skips an empty or non-text value', () => {
    expect(problemReason({ detail: '  ', message: 'M' })).toBe('M')
    expect(problemReason({ detail: 5, error: 'E' })).toBe('E')
    expect(problemReason({ detail: null })).toBeNull()
  })

  it('reads the first model-validation message', () => {
    expect(problemReason({ title: 'One or more validation errors occurred.', errors: { Email: ['The Email field is required.', 'x'], Name: ['n'] } }))
      .toBe('The Email field is required.')
    expect(problemReason({ errors: { Name: [], Email: ['Bad address.'] } })).toBe('Bad address.')
    expect(problemReason({ errors: { Name: 'Plain text.' } })).toBe('Plain text.')
  })

  it('does not use a generic title on its own', () => {
    expect(problemReason({ title: 'Bad Request' })).toBeNull()
  })

  it('gives null for nothing useful', () => {
    expect(problemReason(null)).toBeNull()
    expect(problemReason(undefined)).toBeNull()
    expect(problemReason('text')).toBeNull()
    expect(problemReason({})).toBeNull()
    expect(problemReason({ errors: {} })).toBeNull()
  })
})

describe('problemMessage', () => {
  it('returns the reason from the response body', async () => {
    const response = { json: () => Promise.resolve({ detail: 'A role with that name already exists.' }) }

    expect(await problemMessage(response, 'Save failed: 409')).toBe('A role with that name already exists.')
  })

  it('returns the fallback when the body has no reason', async () => {
    expect(await problemMessage({ json: () => Promise.resolve({ title: 'Conflict' }) }, 'Save failed: 409')).toBe('Save failed: 409')
  })

  it('returns the fallback when the body is not JSON', async () => {
    expect(await problemMessage({ json: () => Promise.reject(new SyntaxError('Unexpected token')) }, 'Save failed: 500')).toBe('Save failed: 500')
  })
})
