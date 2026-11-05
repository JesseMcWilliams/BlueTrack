import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import RiskScoreAdmin from './RiskScoreAdmin.vue'
import RiskScoreDefaults from './RiskScoreDefaults.vue'
import RiskFindings from './RiskFindings.vue'

// D-349: Admin > Risk Score: Bands, Defaults and Findings.

function jsonResponse(body, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body) })
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/admin/risk-score-bands', name: 'admin-risk-score-bands', component: RiskScoreAdmin },
      { path: '/admin/risk-score-bands/new', name: 'admin-risk-score-band-create', component: { template: '<div />' } },
      { path: '/admin/risk-score-bands/:riskScoreBandKey', name: 'admin-risk-score-band-edit', component: { template: '<div />' } },
      { path: '/targets/:targetKey', name: 'target-edit', component: { template: '<div />' } },
      { path: '/accounts/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }
    ]
  })
}

const defaults = () => ({
  defaultAddressTargetRiskScore: 0, defaultTargetRiskScore: 0, defaultAccessGroupRiskScore: 0,
  findingTypes: [
    { findingType: 'DnaHardCodedSecret', displayName: 'Hard-coded secret', description: 'd1', riskValue: 300, isEnabled: true },
    { findingType: 'DnaPassTheHash', displayName: 'Pass-the-Hash vulnerable', description: 'd2', riskValue: 400, isEnabled: true }
  ]
})

describe('RiskScoreAdmin.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    delete globalThis.fetch
  })

  it('opens on Bands, and a tab is kept in the address', async () => {
    globalThis.fetch = vi.fn(url => {
      if (url === '/api/admin/risk-score-bands') return jsonResponse([])
      if (url === '/api/admin/risk-score/defaults') return jsonResponse(defaults())
      throw new Error(`Unexpected fetch: ${url}`)
    })
    const router = makeRouter()
    router.push('/admin/risk-score-bands')
    await router.isReady()
    const wrapper = mount(RiskScoreAdmin, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.find('h2').text()).toBe('Risk Score')
    const tabs = wrapper.findAll('[role="tab"]')
    expect(tabs.map(t => t.text())).toEqual(['Bands', 'Defaults', 'Findings'])
    expect(tabs[0].attributes('aria-selected')).toBe('true')

    await tabs[1].trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query.tab).toBe('defaults')
    expect(wrapper.findComponent(RiskScoreDefaults).exists()).toBe(true)
  })
})

describe('RiskScoreDefaults.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    delete globalThis.fetch
  })

  it('saves the default scores and the finding values, and says when scores will recalculate', async () => {
    const calls = []
    globalThis.fetch = vi.fn((url, options) => {
      calls.push({ url, options })
      if (url === '/api/admin/risk-score/defaults' && !options) return jsonResponse(defaults())
      if (url === '/api/admin/risk-score/defaults') return jsonResponse({ defaults: defaults(), rescore: true })
      throw new Error(`Unexpected fetch: ${url}`)
    })
    const wrapper = mount(RiskScoreDefaults)
    await flushPromises()

    const numbers = wrapper.findAll('input[type="number"]')
    await numbers[1].setValue(150)
    await wrapper.find('input[aria-label="Risk value for Pass-the-Hash vulnerable"]').setValue(500)
    await wrapper.find('input[aria-label="Hard-coded secret counts"]').setValue(false)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const put = calls.find(c => c.options?.method === 'PUT')
    expect(JSON.parse(put.options.body)).toEqual({
      defaultAddressTargetRiskScore: 0, defaultTargetRiskScore: 150, defaultAccessGroupRiskScore: 0,
      findingTypes: [
        { findingType: 'DnaHardCodedSecret', riskValue: 300, isEnabled: false },
        { findingType: 'DnaPassTheHash', riskValue: 500, isEnabled: true }
      ]
    })
    expect(wrapper.find('[role="status"]').text()).toContain('marked to recalculate')
  })

  it('shows the API\'s reason when the values are refused', async () => {
    globalThis.fetch = vi.fn((url, options) => (options
      ? jsonResponse({ detail: 'Default scores are 0 to 1,000.' }, false, 400)
      : jsonResponse(defaults())))
    const wrapper = mount(RiskScoreDefaults)
    await flushPromises()
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toBe('Default scores are 0 to 1,000.')
  })
})

describe('RiskFindings.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    delete globalThis.fetch
  })

  const finding = (overrides = {}) => ({
    findingType: 'DnaHardCodedSecret', findingName: 'Hard-coded secret', riskValue: 300, isEnabled: true,
    targetKey: 5, targetName: 'SRV01', accountKey: null, accountName: null, userName: null, address: null, safeName: null, findingCount: 2,
    ...overrides
  })

  it('lists each finding on its target or account, filters by kind, and refreshes from DNA', async () => {
    const calls = []
    globalThis.fetch = vi.fn((url, options) => {
      calls.push({ url, options })
      if (url === '/api/admin/risk-score/findings') {
        return jsonResponse({
          total: 2, max: 2000,
          findings: [finding(), finding({ findingType: 'DnaPassTheHash', findingName: 'Pass-the-Hash vulnerable', isEnabled: false, targetKey: null, targetName: null, accountKey: 9, userName: 'svc', address: 'db01', safeName: 'Apps', findingCount: 1 })]
        })
      }
      if (url === '/api/admin/risk-score/findings/refresh') return jsonResponse({ total: 2 })
      throw new Error(`Unexpected fetch: ${url}`)
    })
    const router = makeRouter()
    const wrapper = mount(RiskFindings, { global: { plugins: [router] } })
    await flushPromises()

    const rows = wrapper.findAll('tbody tr').map(r => r.text())
    expect(rows[0]).toContain('Target SRV01')
    expect(rows[0]).toContain('300')
    expect(rows[1]).toContain('Account svc at db01')
    expect(rows[1]).toContain('Not counted')

    await wrapper.find('select').setValue('DnaPassTheHash')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)

    await wrapper.findAll('button').find(b => b.text() === 'Refresh from DNA').trigger('click')
    await flushPromises()
    expect(calls.find(c => c.url === '/api/admin/risk-score/findings/refresh').options.method).toBe('POST')
    expect(wrapper.find('[role="status"]').text()).toContain('Findings refreshed')
  })
})
