import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import RiskScoreReport from './RiskScoreReport.vue'

// D-124 Phase 3: this page had no component-level Vitest coverage before
// this phase -- establishes coverage for the new pager UI and the
// two-count summary, following Targets.test.js/AccessGroups.test.js's own
// D-121 mocking style.
function jsonResponse(body, { ok = true, status = 200, totalCount = null, filteredCount = totalCount } = {}) {
  return {
    ok,
    status,
    headers: {
      get: (name) => {
        if (name === 'X-Total-Count') return totalCount !== null ? String(totalCount) : null
        if (name === 'X-Filtered-Count') return filteredCount !== null ? String(filteredCount) : null
        return null
      }
    },
    json: () => Promise.resolve(body)
  }
}

const sampleRow = {
  accountKey: 1,
  accountName: 'svc-web01',
  computedRiskScore: 400,
  overrideRiskScore: null,
  effectiveRiskScore: 400,
  riskScoreBandName: 'Medium',
  isRiskScoreStale: false,
  riskScoreCalculatedDate: '2026-09-01T00:00:00Z'
}

function mockInitialLoad({ rows = [sampleRow], totalCount = rows.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url.startsWith('/api/reports/risk-score')) return Promise.resolve(jsonResponse(rows, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('RiskScoreReport.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the report', async () => {
    mockInitialLoad()
    const wrapper = mount(RiskScoreReport)
    await flushPromises()

    expect(wrapper.text()).toContain('svc-web01')
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ rows: [sampleRow], totalCount: 2380, filteredCount: 2380 })
    const wrapper = mount(RiskScoreReport)
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 2380 matching (2380 total)')
  })

  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ rows: [] })
    const wrapper = mount(RiskScoreReport)
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/risk-score?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('clicking Next advances to page 2, and a subsequent sort click resets back to page 1', async () => {
    mockInitialLoad({ rows: [sampleRow], totalCount: 2380, filteredCount: 2380 })
    const wrapper = mount(RiskScoreReport)
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Page 2 of')

    const accountHeaderButton = wrapper.findAll('th button').find(b => b.text().includes('Account'))
    await accountHeaderButton.trigger('click')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/risk-score?'))
    expect(calls.at(-1)).toContain('page=1')
    expect(calls.at(-1)).toContain('sort=accountName')
  })
})
