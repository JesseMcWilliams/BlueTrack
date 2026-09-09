import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import Targets from './Targets.vue'

// D-121: this page had zero filter/sort/count UI before this work -- these
// tests establish coverage for the new filter dropdowns and the shared
// "Showing N of M total" count summary (FilterCountSummary + useTotalCount),
// following RiskScoreBands.test.js's own globalThis.fetch mocking style
// (the freshest Vitest component-test precedent in this project at the
// time this page was moved out of admin/ and given filter UI for the
// first time).
function jsonResponse(body, { ok = true, status = 200, totalCount = null } = {}) {
  return {
    ok,
    status,
    headers: { get: (name) => (name === 'X-Total-Count' && totalCount !== null ? String(totalCount) : null) },
    json: () => Promise.resolve(body)
  }
}

const sampleTarget = {
  targetKey: 1,
  targetType: 'Server',
  targetName: 'web01',
  applicationKey: null,
  applicationName: null,
  riskScore: 500,
  description: null,
  discoverySource: 'Manual',
  modifiedDate: null,
  identifiers: [{ identifierType: 'Hostname', identifierValue: 'web01.example.com' }]
}

// Mounting triggers: 1) GET /api/applications, then 2) load()'s
// Promise.all([GET /api/admin/targets, GET /api/admin/targets/identifier-types]).
function mockInitialLoad({ targets = [sampleTarget], totalCount = targets.length } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/applications') return Promise.resolve(jsonResponse([{ applicationKey: 1, applicationCode: 'APP1', applicationName: 'App One' }]))
    if (url.startsWith('/api/admin/targets/identifier-types')) return Promise.resolve(jsonResponse([{ identifierType: 'Hostname', matchPriority: 30, requiresReview: false }]))
    if (url.startsWith('/api/admin/targets')) return Promise.resolve(jsonResponse(targets, { totalCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('Targets.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the target inventory', async () => {
    mockInitialLoad()

    const wrapper = mount(Targets)
    await flushPromises()

    expect(wrapper.text()).toContain('web01')
    expect(wrapper.text()).toContain('Hostname=web01.example.com')
  })

  it('shows the "Showing N of M total" count summary once X-Total-Count is known', async () => {
    mockInitialLoad({ targets: [sampleTarget], totalCount: 42 })

    const wrapper = mount(Targets)
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1 of 42 total')
  })

  it('does not show the count summary before the first load resolves its header', async () => {
    globalThis.fetch = vi.fn(() => new Promise(() => {})) // never resolves
    const wrapper = mount(Targets)

    expect(wrapper.find('.filter-count-summary').exists()).toBe(false)
  })

  it('sends the Type filter as a targetType query param', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets)
    await flushPromises()

    await wrapper.get('select').setValue('Server')
    await flushPromises()

    const targetsCalls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/targets?'))
    expect(targetsCalls.some(u => u.includes('targetType=Server'))).toBe(true)
  })

  it('opens the New Target form with defaults on + New Target', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets)
    await flushPromises()

    await wrapper.get('button.btn-primary').trigger('click')

    expect(wrapper.find('form').exists()).toBe(true)
    expect(wrapper.text()).toContain('New Target')
  })
})
