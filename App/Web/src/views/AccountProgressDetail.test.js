import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import AccountProgressDetail from './AccountProgressDetail.vue'

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/accounts', name: 'account-progress-list', component: { template: '<div />' } }
    ]
  })
}

// D-130: this form had grown into one long scroll (main fields, Risk
// Score, Risk Exception) -- split into ARIA APG-pattern tabs. This is the
// first Vitest coverage this component has ever had (it makes 5+
// concurrent fetches plus an implicit lock-acquire on mount, which is why
// no prior test existed) -- the rights store is pre-populated directly
// rather than mocking /api/me, since Pinia state is simpler to control
// than a fetch response for something ensureLoaded() only calls once.
const RISK_ACCEPTED_STATUS_KEY = 99

function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, text: () => Promise.resolve(JSON.stringify(body)), json: () => Promise.resolve(body) }
}
function emptyResponse() {
  return { ok: true, status: 200, text: () => Promise.resolve(''), json: () => Promise.resolve(null) }
}

const fieldMetadata = [
  { fieldName: 'CurrentStageKey', displayLabel: 'Stage', fieldType: 'Dropdown', isRequired: true, displayOrder: 1, referenceTable: 'dim_blueprint_stage' },
  { fieldName: 'CurrentStatusKey', displayLabel: 'Status', fieldType: 'Dropdown', isRequired: true, displayOrder: 2, referenceTable: 'dim_progress_status' }
]

const referenceData = {
  dim_blueprint_stage: [{ key: 1, name: 'Discovered' }],
  dim_progress_status: [
    { key: 1, name: 'Not Started' },
    { key: RISK_ACCEPTED_STATUS_KEY, name: 'Risk Accepted / Excluded' }
  ]
}

function baseDetail(overrides = {}) {
  return {
    progressKey: 1,
    accountKey: 42,
    accountName: 'test-account',
    currentStageKey: 1,
    currentStatusKey: 1,
    riskLevelKey: null,
    accountTypeKey: null,
    sorKey: null,
    ownerName: null,
    businessUnit: null,
    targetRemediationDate: null,
    actualCompletionDate: null,
    notes: null,
    lastUpdated: '2026-01-01T00:00:00Z',
    exceptionKey: null,
    computedRiskScore: 100,
    overrideRiskScore: null,
    effectiveRiskScore: 100,
    riskScoreBandName: 'Low',
    ...overrides
  }
}

function mockLoad({ detailOverrides = {} } = {}) {
  globalThis.fetch = vi.fn((url, options) => {
    if (url === '/api/account-progress/field-metadata') return Promise.resolve(jsonResponse(fieldMetadata))
    if (url === '/api/account-progress/reference-data') return Promise.resolve(jsonResponse(referenceData))
    if (url === '/api/account-progress/42' && (!options || options.method === undefined)) return Promise.resolve(jsonResponse(baseDetail(detailOverrides)))
    if (url === '/api/account-progress/42/lock' && (!options || !options.method)) return Promise.resolve(emptyResponse()) // no existing lock
    if (url === '/api/account-progress/42/lock' && options?.method === 'POST') {
      return Promise.resolve(jsonResponse({ lockedByUserKey: 1, lockedByName: 'Test User', lockedAt: '2026-01-01T00:00:00Z' }))
    }
    if (url === '/api/account-progress/42/application-exceptions') return Promise.resolve(emptyResponse())
    if (url === '/api/risk-exceptions') return Promise.resolve(jsonResponse([]))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

async function mountEditable(options = {}) {
  const wrapper = mount(AccountProgressDetail, {
    props: { accountKey: 42 },
    global: { plugins: [makeRouter()] },
    ...options
  })
  await flushPromises()
  await flushPromises() // acquireLock's own await chain needs a second flush
  return wrapper
}

describe('AccountProgressDetail.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    const rights = useRightsStore()
    rights.loaded = true
    rights.permissionNames = ['EditAccountProgress']
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('defaults to the Details tab, with Details/Risk Score visible and Risk Exception absent (not Risk Accepted)', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    expect(wrapper.get('#account-progress-tab-details').attributes('aria-selected')).toBe('true')
    expect(wrapper.find('#account-progress-tab-risk-score').exists()).toBe(true)
    expect(wrapper.find('#account-progress-tab-risk-exception').exists()).toBe(false)
  })

  it('clicking the Risk Score tab shows that panel and hides Details', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    await wrapper.get('#account-progress-tab-risk-score').trigger('click')

    expect(wrapper.get('#account-progress-tab-risk-score').attributes('aria-selected')).toBe('true')
    // jsdom's own getComputedStyle doesn't reliably reflect an inline
    // v-show-applied "display: none" back through VTU's isVisible() helper
    // in this environment -- check the actual rendered style attribute
    // directly instead (confirmed correct via the real rendered HTML).
    expect(wrapper.get('#account-progress-panel-details').attributes('style')).toContain('display: none')
    expect(wrapper.get('#account-progress-panel-risk-score').attributes('style')).toBeUndefined()
    expect(wrapper.text()).toContain('Calculated Risk')
  })

  it('ArrowRight moves from Details to Risk Score', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    await wrapper.get('#account-progress-tab-details').trigger('keydown', { key: 'ArrowRight' })

    expect(wrapper.get('#account-progress-tab-risk-score').attributes('aria-selected')).toBe('true')
  })

  it('shows a Risk Exception tab when status is Risk Accepted / Excluded', async () => {
    mockLoad({ detailOverrides: { currentStatusKey: RISK_ACCEPTED_STATUS_KEY } })
    const wrapper = await mountEditable()
    await flushPromises() // the isRiskAccepted watcher's own loadLinkableExceptions() fetch

    expect(wrapper.find('#account-progress-tab-risk-exception').exists()).toBe(true)
  })
})
