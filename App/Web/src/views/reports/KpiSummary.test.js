import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import KpiSummary from './KpiSummary.vue'

// D-143: covers the four ratio computations and the zero-denominator guard
// (a brand new environment with no accounts onboarded yet shouldn't divide
// by zero and show NaN%/Infinity%).
function jsonResponse(body) {
  return { ok: true, json: () => Promise.resolve(body) }
}

function mockSummary(summary) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/reports/kpi-summary') return Promise.resolve(jsonResponse(summary))
    return Promise.resolve({ ok: false, status: 404 })
  })
}

describe('KpiSummary.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('renders each ratio as a percentage of the correct denominator', async () => {
    mockSummary({
      totalAccounts: 100,
      inScopeAccounts: 80,
      onboardedAccounts: 40,
      managedAccounts: 20,
      compliantAccounts: 10
    })
    const wrapper = mount(KpiSummary)
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('80%') // In Scope / Total = 80/100
    expect(text).toContain('50%') // Onboarded / In Scope = 40/80
    expect(text).toContain('20 / 40')
    expect(text).toContain('10 / 20')
    expect(text).toContain('100 account(s) total.')
  })

  it('shows N/A instead of dividing by zero when a denominator is empty', async () => {
    mockSummary({
      totalAccounts: 0,
      inScopeAccounts: 0,
      onboardedAccounts: 0,
      managedAccounts: 0,
      compliantAccounts: 0
    })
    const wrapper = mount(KpiSummary)
    await flushPromises()

    expect(wrapper.text()).toContain('N/A')
    expect(wrapper.text()).not.toContain('NaN')
    expect(wrapper.text()).not.toContain('Infinity')
  })

  it('shows an error message when the request fails', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: false, status: 500 }))
    const wrapper = mount(KpiSummary)
    await flushPromises()

    expect(wrapper.find('[role="alert"]').exists()).toBe(true)
  })
})
