import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { formatCount } from './formatCount'
import FilterCountSummary from '../components/FilterCountSummary.vue'

// D-218: counts use the number format of the browser's language. The tests
// compare against Intl.NumberFormat's default-locale output rather than a
// hardcoded "12,345", so they pass whatever locale the test host uses.
describe('formatCount (D-218)', () => {
  it('is blank for null, undefined and an empty string', () => {
    expect(formatCount(null)).toBe('')
    expect(formatCount(undefined)).toBe('')
    expect(formatCount('')).toBe('')
  })

  it('formats a number in the default locale', () => {
    expect(formatCount(1234567)).toBe(new Intl.NumberFormat().format(1234567))
    expect(formatCount(0)).toBe(new Intl.NumberFormat().format(0))
  })

  it('formats a numeric string', () => {
    expect(formatCount('12345')).toBe(new Intl.NumberFormat().format(12345))
  })

  it('returns a non-numeric string unchanged', () => {
    expect(formatCount('n/a')).toBe('n/a')
  })

  it('is used by FilterCountSummary for a large count', () => {
    const wrapper = mount(FilterCountSummary, {
      props: { shown: 50, filteredCount: 12345, total: 23456, page: 2, pageSize: 50 }
    })
    const fmt = new Intl.NumberFormat()
    const text = wrapper.text()
    expect(text).toContain(`of ${fmt.format(12345)} matching`)
    expect(text).toContain(`(${fmt.format(23456)} total)`)
  })
})
