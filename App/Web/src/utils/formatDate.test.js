import { describe, it, expect, afterEach } from 'vitest'
import { formatDate } from './formatDate'
import { setFormatLocale } from './formatLocale'

// D-294: the date part, in the viewer's date format, never moved to another day by a time zone.
describe('formatDate', () => {
  afterEach(() => setFormatLocale(undefined))

  it('follows the format locale', () => {
    setFormatLocale('en-US')
    expect(formatDate('2026-10-21T00:00:00')).toBe('10/21/2026')
    setFormatLocale('en-GB')
    expect(formatDate('2026-10-21')).toBe('21/10/2026')
    setFormatLocale('de-DE')
    expect(formatDate('2026-10-21T23:59:59.1234567')).toBe('21.10.2026')
  })

  it('keeps the calendar date written in the value', () => {
    setFormatLocale('en-US')
    expect(formatDate('2026-01-01T00:00:00')).toBe('01/01/2026')
    expect(formatDate('2026-12-31T23:30:00')).toBe('12/31/2026')
  })

  it('is blank for no value and leaves a value that is not a date as it is', () => {
    expect(formatDate(null)).toBe('')
    expect(formatDate('')).toBe('')
    expect(formatDate('soon')).toBe('soon')
  })
})
