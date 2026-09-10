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

// D-132: RiskLevelKey/BusinessUnit/TargetRemediationDate added alongside
// the pre-existing Stage/Status so the read-only-view test below exercises
// all three of displayValueFor()'s branches (Dropdown/Text/Date), not just
// Dropdown -- the real account_progress_field_metadata seed has 10 fields
// total; these 5 are enough to cover the rendering logic without
// replicating the full seed here.
const fieldMetadata = [
  { fieldName: 'CurrentStageKey', displayLabel: 'Stage', fieldType: 'Dropdown', isRequired: true, displayOrder: 1, referenceTable: 'dim_blueprint_stage' },
  { fieldName: 'CurrentStatusKey', displayLabel: 'Status', fieldType: 'Dropdown', isRequired: true, displayOrder: 2, referenceTable: 'dim_progress_status' },
  { fieldName: 'RiskLevelKey', displayLabel: 'Risk Level', fieldType: 'Dropdown', isRequired: false, displayOrder: 3, referenceTable: 'dim_risk_level' },
  { fieldName: 'BusinessUnit', displayLabel: 'Business Unit', fieldType: 'Text', isRequired: false, displayOrder: 4, referenceTable: null },
  { fieldName: 'TargetRemediationDate', displayLabel: 'Target Remediation Date', fieldType: 'Date', isRequired: false, displayOrder: 5, referenceTable: null }
]

const referenceData = {
  dim_blueprint_stage: [{ key: 1, name: 'Discovered' }],
  dim_progress_status: [
    { key: 1, name: 'Not Started' },
    { key: RISK_ACCEPTED_STATUS_KEY, name: 'Risk Accepted / Excluded' }
  ],
  dim_risk_level: [{ key: 5, name: 'High' }]
}

function baseDetail(overrides = {}) {
  return {
    progressKey: 1,
    accountKey: 42,
    accountName: 'test-account',
    address: '10.0.0.42',
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

function mockLoad({ detailOverrides = {}, recalculatedDetailOverrides = { computedRiskScore: 250, riskScoreBandName: 'Medium' } } = {}) {
  let recalculated = false
  globalThis.fetch = vi.fn((url, options) => {
    if (url === '/api/account-progress/field-metadata') return Promise.resolve(jsonResponse(fieldMetadata))
    if (url === '/api/account-progress/reference-data') return Promise.resolve(jsonResponse(referenceData))
    if (url === '/api/account-progress/42' && (!options || options.method === undefined)) {
      return Promise.resolve(jsonResponse(baseDetail(recalculated ? { ...detailOverrides, ...recalculatedDetailOverrides } : detailOverrides)))
    }
    if (url === '/api/account-progress/42/lock' && (!options || !options.method)) return Promise.resolve(emptyResponse()) // no existing lock
    if (url === '/api/account-progress/42/lock' && options?.method === 'POST') {
      return Promise.resolve(jsonResponse({ lockedByUserKey: 1, lockedByName: 'Test User', lockedAt: '2026-01-01T00:00:00Z' }))
    }
    if (url === '/api/account-progress/42/application-exceptions') return Promise.resolve(emptyResponse())
    if (url === '/api/account-progress/42/recalculate-risk-score' && options?.method === 'POST') {
      recalculated = true
      return Promise.resolve(emptyResponse())
    }
    if (url === '/api/risk-exceptions') return Promise.resolve(jsonResponse([]))
    if (/^\/api\/risk-exceptions\/\d+$/.test(url)) {
      return Promise.resolve(jsonResponse({
        exceptionKey: 7, exceptionID: 'EX-7', justification: 'Approved by CISO',
        reviewDate: '2026-06-01T00:00:00', statusName: 'Active'
      }))
    }
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

  // D-133: Account Name/Address are fact_account's own read-only context,
  // not part of the editable fact_account_progress field set -- shown above
  // the tabs (not inside any one of them) so they're visible regardless of
  // which tab is active.
  it('shows Account Name and Address above the tabs, regardless of the active tab', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    expect(wrapper.text()).toContain('Account Nametest-account')
    expect(wrapper.text()).toContain('Address10.0.0.42')
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
    expect(wrapper.text()).toContain('Effective Risk Score')
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

  // D-131: a per-account "Recalculate" action on the Risk Score tab --
  // posts to its own dedicated endpoint (distinct from the Risk Score
  // report's bulk "Recalculate Now") and refreshes this one account's
  // Calculated Risk/Risk Band from the response.
  it('Recalculate posts to the per-account endpoint and refreshes Calculated Risk/Risk Band', async () => {
    mockLoad()
    const wrapper = await mountEditable()
    await wrapper.get('#account-progress-tab-risk-score').trigger('click')

    expect(wrapper.get('#account-progress-panel-risk-score').text()).toContain('Calculated Risk100')

    await wrapper.get('button.account-progress-recalculate').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/account-progress/42/recalculate-risk-score', { method: 'POST' })
    expect(wrapper.get('#account-progress-panel-risk-score').text()).toContain('Calculated Risk250')
    expect(wrapper.get('#account-progress-panel-risk-score').text()).toContain('Risk BandMedium')
  })

  // D-132: a viewer with no EditAccountProgress permission never acquires
  // the lock, so this exercises the read-only <dl> -- confirmed to have
  // been missing most of the model (Risk Level/Account Type/SOR/Business
  // Unit/dates/Effective Risk Score/Override/Last Updated/linked Risk
  // Exception) and showing Stage/Status as raw keys instead of resolved
  // names before this fix.
  it('read-only view (no edit permission) shows every field with resolved names, not raw keys', async () => {
    const rights = useRightsStore()
    rights.permissionNames = []
    mockLoad({
      detailOverrides: {
        riskLevelKey: 5,
        businessUnit: 'Finance',
        targetRemediationDate: '2026-03-01T00:00:00',
        overrideRiskScore: 400,
        effectiveRiskScore: 400,
        exceptionKey: 7
      }
    })
    const wrapper = await mountEditable()

    expect(wrapper.find('form').exists()).toBe(false)
    const text = wrapper.text()
    expect(text).toContain('Account Nametest-account')
    expect(text).toContain('Address10.0.0.42')
    expect(text).toContain('StageDiscovered')
    expect(text).toContain('StatusNot Started')
    expect(text).toContain('Risk LevelHigh')
    expect(text).toContain('Business UnitFinance')
    expect(text).toContain('Target Remediation Date2026-03-01')
    expect(text).toContain('Last Updated2026-01-01')
    expect(text).toContain('Calculated Risk100')
    expect(text).toContain('Effective Risk Score400')
    expect(text).toContain('Override Risk Score400')
    expect(text).toContain('Risk ExceptionEX-7 (Active) — Approved by CISO, reviewed 2026-06-01')
  })
})
