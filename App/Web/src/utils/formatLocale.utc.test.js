import { describe, it, expect } from 'vitest'
import { formatUtcDateTime, formatDateTime } from './formatLocale'

// UI review W2-4: a time the API sends without a zone is UTC (SQL datetime2), so it must be read as UTC, not as the
// browser's local time.
describe('formatUtcDateTime', () => {
  it('reads a zone-less time as UTC', () => {
    expect(formatUtcDateTime('2026-01-01T12:00:00')).toBe(formatDateTime(new Date(Date.UTC(2026, 0, 1, 12, 0, 0))))
  })

  it('uses a time that already has a zone as it is', () => {
    expect(formatUtcDateTime('2026-01-01T12:00:00Z')).toBe(formatDateTime(new Date(Date.UTC(2026, 0, 1, 12, 0, 0))))
    expect(formatUtcDateTime('2026-01-01T12:00:00+02:00')).toBe(formatDateTime(new Date(Date.UTC(2026, 0, 1, 10, 0, 0))))
  })

  it('gives nothing for nothing, and the text back for a value that is not a date', () => {
    expect(formatUtcDateTime(null)).toBe('')
    expect(formatUtcDateTime('')).toBe('')
    expect(formatUtcDateTime('not a date')).toBe('not a date')
  })

  it('does not show the raw stored text', () => {
    expect(formatUtcDateTime('2026-01-01T12:00:00')).not.toContain('T12:00:00')
  })
})
