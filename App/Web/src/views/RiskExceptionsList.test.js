import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import RiskExceptionsList from './RiskExceptionsList.vue'

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

const sampleException = {
  exceptionKey: 1,
  exceptionID: 'EXC-2026-0001',
  scopeType: 'Account',
  scopeName: 'svc-web01',
  justification: 'Vendor delay',
  approvedByName: 'Jane Analyst',
  approvalDate: '2026-01-01',
  reviewDate: '2026-06-01',
  statusName: 'Active'
}

function mockInitialLoad({ exceptions = [sampleException], totalCount = exceptions.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url.startsWith('/api/risk-exceptions')) return Promise.resolve(jsonResponse(exceptions, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/risk-exceptions/new', name: 'risk-exception-create', component: { template: '<div />' } },
      { path: '/risk-exceptions/:exceptionKey', name: 'risk-exception-edit', component: { template: '<div />' } }
    ]
  })
}

describe('RiskExceptionsList.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the exceptions list', async () => {
    mockInitialLoad()
    const wrapper = mount(RiskExceptionsList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('EXC-2026-0001')
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ exceptions: [sampleException], totalCount: 60, filteredCount: 12 })
    const wrapper = mount(RiskExceptionsList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 12 matching (60 total)')
  })

  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ exceptions: [] })
    const wrapper = mount(RiskExceptionsList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/risk-exceptions?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('clicking Next advances to page 2, and a subsequent filter change resets back to page 1', async () => {
    mockInitialLoad({ exceptions: [sampleException], totalCount: 150, filteredCount: 150 })
    const wrapper = mount(RiskExceptionsList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Page 2 of')

    const statusSelect = wrapper.findAll('select')[0]
    await statusSelect.setValue('Active')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/risk-exceptions?'))
    expect(calls.at(-1)).toContain('page=1')
    expect(calls.at(-1)).toContain('status=Active')
  })
})
