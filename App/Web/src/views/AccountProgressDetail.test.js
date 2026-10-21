import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import { useListStateStore } from '../stores/listState'
import AccountProgressDetail from './AccountProgressDetail.vue'

// UI review W2-4: Force Release asks first (the shared dialog is mocked, as on the other pages).
vi.mock('../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../composables/useConfirmDialog'

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/accounts', name: 'account-progress-list', component: { template: '<div />' } },
      { path: '/accounts/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }
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
  { fieldName: 'ApplicationKey', displayLabel: 'Application', fieldType: 'Dropdown', isRequired: false, displayOrder: 3.5, referenceTable: 'dim_application' },   // D-238
  { fieldName: 'BusinessUnit', displayLabel: 'Business Unit', fieldType: 'Text', isRequired: false, displayOrder: 4, referenceTable: null },
  { fieldName: 'TargetRemediationDate', displayLabel: 'Target Remediation Date', fieldType: 'Date', isRequired: false, displayOrder: 5, referenceTable: null }
]

const referenceData = {
  dim_blueprint_stage: [{ key: 1, name: 'Discovered' }],
  dim_progress_status: [
    { key: 1, name: 'Not Started' },
    { key: RISK_ACCEPTED_STATUS_KEY, name: 'Risk Accepted / Excluded' }
  ],
  dim_risk_level: [{ key: 5, name: 'High' }],
  dim_application: [{ key: 3, name: 'Billing' }, { key: 4, name: 'Payroll' }]
}

function baseDetail(overrides = {}) {
  return {
    progressKey: 1,
    accountKey: 42,
    accountName: 'test-account',
    userName: 'test-user',
    address: '10.0.0.42',
    currentStageKey: 1,
    currentStatusKey: 1,
    riskLevelKey: null,
    accountTypeKey: null,
    sorKey: null,
    applicationKey: null,
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

function mockLoad({ detailOverrides = {}, recalculatedDetailOverrides = { computedRiskScore: 250, riskScoreBandName: 'Medium' }, existingLock = null } = {}) {
  let recalculated = false
  globalThis.fetch = vi.fn((url, options) => {
    if (url === '/api/account-progress/field-metadata') return Promise.resolve(jsonResponse(fieldMetadata))
    if (url === '/api/account-progress/reference-data') return Promise.resolve(jsonResponse(referenceData))
    if (url === '/api/account-progress/42' && (!options || options.method === undefined)) {
      return Promise.resolve(jsonResponse(baseDetail(recalculated ? { ...detailOverrides, ...recalculatedDetailOverrides } : detailOverrides)))
    }
    if (url === '/api/account-progress/42/lock' && (!options || !options.method)) {
      return Promise.resolve(existingLock ? jsonResponse(existingLock) : emptyResponse()) // emptyResponse = no existing lock
    }
    if (url === '/api/account-progress/42/lock' && options?.method === 'POST') {
      return Promise.resolve(jsonResponse({ lockedByUserKey: 1, lockedByName: 'Test User', lockedAt: '2026-01-01T00:00:00Z' }))
    }
    if (url === '/api/account-progress/42/application-exceptions') return Promise.resolve(emptyResponse())
    if (url === '/api/account-progress/42/safe-access') return Promise.resolve(jsonResponse({ people: [], applications: [], adReadAt: null }))
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

describe('AccountProgressDetail.vue: a lost edit lock (UI review W2-3)', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    const rights = useRightsStore()
    rights.userKey = 1
    rights.permissionNames = ['EditAccountProgress']
    rights.loaded = true
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  async function mountAndLoseTheLock(reacquire) {
    mockLoad()
    const wrapper = await mountEditable()
    const base = globalThis.fetch.getMockImplementation()
    globalThis.fetch = vi.fn((url, options) => {
      if (url === '/api/account-progress/42/lock/heartbeat') return Promise.resolve({ ok: false, status: 409, json: () => Promise.resolve({}) })
      if (url === '/api/account-progress/42/lock' && options?.method === 'POST' && reacquire) return Promise.resolve(reacquire)
      return base(url, options)
    })
    await vi.advanceTimersByTimeAsync(60000)
    await flushPromises()
    return wrapper
  }

  it('warns at the next heartbeat, keeps the form and what was typed, and stops Save', async () => {
    const wrapper = await mountAndLoseTheLock()
    const field = wrapper.findAll('form input').find(i => !i.attributes('type') || i.attributes('type') === 'text')
    await field.setValue('typed before the lock was lost')
    expect(wrapper.find('[role="alert"]').text()).toContain('You no longer hold the edit lock')
    expect(field.element.value).toBe('typed before the lock was lost')
    expect(wrapper.find('button.btn-primary[type="submit"]').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).not.toContain('Currently being edited')
  })

  it('Take the lock again gets Save back without reloading', async () => {
    const wrapper = await mountAndLoseTheLock({ ok: true, status: 200, json: () => Promise.resolve({ lockedByUserKey: 1, lockedByName: 'Test User', lockedAt: '2026-01-01T00:00:00Z' }) })

    await wrapper.findAll('button').find(b => b.text() === 'Take the lock again').trigger('click')
    await flushPromises()

    expect(wrapper.text()).not.toContain('You no longer hold the edit lock')
    expect(wrapper.find('button.btn-primary[type="submit"]').attributes('disabled')).toBeUndefined()
  })

  it('says who has it when a colleague holds the lock now', async () => {
    const wrapper = await mountAndLoseTheLock({ ok: false, status: 409, json: () => Promise.resolve({ lockedByUserKey: 2, lockedByName: 'Other User', lockedAt: '2026-01-01T00:00:00Z' }) })

    await wrapper.findAll('button').find(b => b.text() === 'Take the lock again').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Other User now holds the edit lock')
    expect(wrapper.find('button.btn-primary[type="submit"]').attributes('disabled')).toBeDefined()
  })
})

describe('AccountProgressDetail.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    const rights = useRightsStore()
    rights.loaded = true
    rights.permissionNames = ['EditAccountProgress']
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  // D-177: a lock the current user already holds (another tab, or an
  // earlier visit that left without saving) is taken over, not shown as
  // "Currently being edited by <themselves>"; someone else's still isn't.
  it('takes over an edit lock held by the same user', async () => {
    useRightsStore().userKey = 1
    mockLoad({ existingLock: { lockedByUserKey: 1, lockedByName: 'Test User', lockedAt: '2026-01-01T00:00:00Z' } })
    const wrapper = await mountEditable()

    const acquireCalls = globalThis.fetch.mock.calls.filter(([url, options]) => url === '/api/account-progress/42/lock' && options?.method === 'POST')
    expect(acquireCalls).toHaveLength(1)
    expect(wrapper.find('form button[type="submit"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('Currently being edited by')
  })

  it('does not take over an edit lock held by someone else', async () => {
    useRightsStore().userKey = 1
    mockLoad({ existingLock: { lockedByUserKey: 2, lockedByName: 'Other User', lockedAt: '2026-01-01T00:00:00Z' } })
    const wrapper = await mountEditable()

    const acquireCalls = globalThis.fetch.mock.calls.filter(([url, options]) => url === '/api/account-progress/42/lock' && options?.method === 'POST')
    expect(acquireCalls).toHaveLength(0)
    expect(wrapper.find('form button[type="submit"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Currently being edited by Other User')
  })

  it('defaults to the Details tab, with Details/Risk Score visible and Risk Exception absent (not Risk Accepted)', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    expect(wrapper.get('#account-progress-tab-details').attributes('aria-selected')).toBe('true')
    expect(wrapper.find('#account-progress-tab-risk-score').exists()).toBe(true)
    expect(wrapper.find('#account-progress-tab-risk-exception').exists()).toBe(false)
  })

  // D-133/D-134: Username/Address/Account Name are fact_account's own
  // read-only context, not part of the editable fact_account_progress field
  // set -- shown once, above the tabs (not inside any one of them, and not
  // duplicated between the edit form and the read-only view), so they're
  // visible regardless of which tab is active or whether the viewer holds
  // the edit lock. Username and Address sit on one line; Account Name is a
  // secondary line below it (2026-09-10 layout request).
  it('shows Username and Address on one line, with Account Name below, above the tabs', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    const identity = wrapper.get('.account-progress-identity')
    const primaryText = identity.get('.account-progress-identity-primary').text()
    expect(primaryText).toContain('Username: test-user')
    expect(primaryText).toContain('Address: 10.0.0.42')
    expect(identity.get('.account-progress-identity-secondary').text()).toContain('Account Name: test-account')
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

  // UI review W2-8
  it('explains that the risk override saves on its own, and says when it has saved', async () => {
    mockLoad()
    const wrapper = await mountEditable()
    await wrapper.get('#account-progress-tab-risk-score').trigger('click')
    expect(wrapper.text()).toContain('saved on its own with Save Override')
    const base = globalThis.fetch.getMockImplementation()
    globalThis.fetch = vi.fn((url, options) => (url === '/api/account-progress/42/risk-score-override' && options?.method === 'PUT'
      ? Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) })
      : base(url, options)))

    await wrapper.findAll('button').find(b => b.text() === 'Save Override').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Risk score override saved.')
  })

  // UI review W2-5
  it('tells a view-only user why there is no edit form', async () => {
    const rights = useRightsStore()
    rights.permissionNames = []
    mockLoad()
    const wrapper = await mountEditable()
    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.text()).toContain('You can view this account but not change it')
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
        applicationKey: 3,
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
    expect(text).toContain('Username: test-user')
    expect(text).toContain('Address: 10.0.0.42')
    expect(text).toContain('Account Name: test-account')
    expect(text).toContain('StageDiscovered')
    expect(text).toContain('StatusNot Started')
    expect(text).toContain('Risk LevelHigh')
    expect(text).toContain('ApplicationBilling')   // D-238
    expect(text).toContain('Business UnitFinance')
    expect(text).toContain('Target Remediation Date2026-03-01')
    expect(text).toContain('Last Updated2026-01-01')
    expect(text).toContain('Calculated Risk100')
    expect(text).toContain('Effective Risk Score400')
    expect(text).toContain('Override Risk Score400')
    expect(text).toContain('Risk ExceptionEX-7 (Active) — Approved by CISO, reviewed 2026-06-01')
  })

  // UI review W2-2: Save and Cancel used to land on an unfiltered page 1 of the list.
  describe('returning to the list', () => {
    function listRouter() {
      return createRouter({
        history: createMemoryHistory(),
        routes: [
          { path: '/', name: 'home', component: { template: '<div />' } },
          { path: '/accounts', name: 'account-progress-list', component: { template: '<div />' } }
        ]
      })
    }

    async function mountWith(router) {
      useRightsStore().userKey = 1
      mockLoad()
      await router.push('/')
      return mountEditable({ global: { plugins: [router] } })
    }

    it('Cancel goes back to the list with the filters, sort and page it had', async () => {
      useListStateStore().remember('account-progress-list', { stage: 'Discovery', sort: 'userName:desc', page: '2' })
      const router = listRouter()
      const wrapper = await mountWith(router)

      await wrapper.findAll('button').find(b => b.text() === 'Cancel').trigger('click')
      await flushPromises()

      expect(router.currentRoute.value.name).toBe('account-progress-list')
      expect(router.currentRoute.value.query).toEqual({ stage: 'Discovery', sort: 'userName:desc', page: '2' })
    })

    it('Save goes back to the list the same way', async () => {
      useListStateStore().remember('account-progress-list', { search: 'svc' })
      const router = listRouter()
      const wrapper = await mountWith(router)
      const base = globalThis.fetch.getMockImplementation()
      globalThis.fetch.mockImplementation((url, options) => url === '/api/account-progress/42' && options?.method === 'PUT'
        ? Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) })
        : base(url, options))

      await wrapper.find('form').trigger('submit')
      await flushPromises()

      expect(router.currentRoute.value.name).toBe('account-progress-list')
      expect(router.currentRoute.value.query).toEqual({ search: 'svc' })
    })

    it('with no remembered view it goes to the plain list', async () => {
      const router = listRouter()
      const wrapper = await mountWith(router)

      await wrapper.findAll('button').find(b => b.text() === 'Cancel').trigger('click')
      await flushPromises()

      expect(router.currentRoute.value.name).toBe('account-progress-list')
      expect(router.currentRoute.value.query).toEqual({})
    })
  })

  describe('Force Release Lock (UI review W2-4)', () => {
    const heldByOther = { lockedByUserKey: 2, lockedByName: 'Other User', lockedAt: '2026-01-01T00:00:00' }
    const releaseButton = (wrapper) => wrapper.findAll('button').find(b => b.text() === 'Force Release Lock')
    const posts = (suffix) => globalThis.fetch.mock.calls.filter(([url, options]) => url === `/api/account-progress/42/${suffix}` && options?.method === 'POST')

    it('shows when the lock was taken as a local date and time, not the raw stored text', async () => {
      useRightsStore().userKey = 1
      mockLoad({ existingLock: heldByOther })
      const wrapper = await mountEditable()

      const text = wrapper.text()
      expect(text).toContain('Currently being edited by Other User since')
      expect(text).not.toContain('2026-01-01T00:00:00')
    })

    it('asks first, naming the holder, and does nothing when declined', async () => {
      confirmDelete.mockResolvedValue(false)
      useRightsStore().userKey = 1
      mockLoad({ existingLock: heldByOther })
      const wrapper = await mountEditable()

      await releaseButton(wrapper).trigger('click')
      await flushPromises()

      expect(confirmDelete).toHaveBeenCalledTimes(1)
      const [message, label] = confirmDelete.mock.calls[0]
      expect(message.title).toBe('Take over editing from Other User?')
      expect(message.details[0]).toContain('Other User has had this account open for editing since')
      expect(message.warning).toBe('Anything Other User has typed and not saved will be lost.')
      expect(label).toBe('Force Release Lock')
      expect(posts('lock/force-release')).toHaveLength(0)
      expect(wrapper.text()).toContain('Currently being edited by Other User')
    })

    it('a confirmed release takes the lock and opens the edit form', async () => {
      useRightsStore().userKey = 1
      mockLoad({ existingLock: heldByOther })
      const base = globalThis.fetch.getMockImplementation()
      globalThis.fetch.mockImplementation((url, options) => url === '/api/account-progress/42/lock/force-release' && options?.method === 'POST'
        ? Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) })
        : base(url, options))
      const wrapper = await mountEditable()

      await releaseButton(wrapper).trigger('click')
      await flushPromises()
      await flushPromises()

      expect(posts('lock/force-release')).toHaveLength(1)
      expect(posts('lock')).toHaveLength(1)   // it then takes the lock for itself
      expect(wrapper.find('form button[type="submit"]').exists()).toBe(true)
    })
  })
})

// D-277: an account deleted in CyberArk after the switch to Direct because its Safe is a CyberArk internal one.
describe('AccountProgressDetail.vue: an account in a CyberArk internal Safe', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    useRightsStore().permissionNames = ['ViewDashboard']
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('says it was deleted in CyberArk because its Safe is internal', async () => {
    mockLoad({ detailOverrides: { isDeleted: true, isDeletedInSource: true, isInIgnoredSafe: false, isInInternalSafe: true } })
    const wrapper = await mountEditable()

    expect(wrapper.find('.deleted-notice').text()).toContain("Deleted in CyberArk: its Safe is a CyberArk internal Safe, which PVWA doesn't return.")
  })

  it('shows when CyberArk created the account and its last CPM action (Self-Hosted Direct)', async () => {
    mockLoad({ detailOverrides: { createdTime: '2026-09-01T08:30:00', lastCPMAction: 'Change', lastCPMActionResult: 'Failure', lastCPMActionTime: '2026-10-19T04:00:00' } })
    const wrapper = await mountEditable()

    const facts = wrapper.find('.cpm-facts').text()
    expect(facts).toContain('Created in CyberArk:')
    expect(facts).toContain('Last CPM action: Change (Failure),')
  })

  it('has no CPM line for an account without those fields', async () => {
    mockLoad()
    const wrapper = await mountEditable()

    expect(wrapper.find('.cpm-facts').exists()).toBe(false)
    expect(wrapper.find('.classic-facts').exists()).toBe(false)
  })

  // D-283: the Progress Rule that moved the account to its stage.
  it('says which Progress Rule moved the account to its stage', async () => {
    mockLoad({ detailOverrides: { lastAdvancedByRuleName: 'In the Vault', lastAdvancedAt: '2026-10-21T02:05:00' } })
    const wrapper = await mountEditable()

    expect(wrapper.find('.moved-by-rule').text()).toContain('Moved to this stage by the Progress Rule "In the Vault" on')
  })

  // D-280: the classic account details; a linked account in BlueTrack is a link to its page.
  it('shows the device type, how it was created and its linked accounts', async () => {
    mockLoad({ detailOverrides: {
      deviceType: 'Operating System', creationMethod: 'PVWA',
      linkedLogonAccount: 'Logon Safe\\logon-acct', linkedLogonAccountKey: null,
      linkedReconcileAccount: 'Rec Safe\\rec-acct', linkedReconcileAccountKey: 77
    } })
    const wrapper = await mountEditable()

    const facts = wrapper.find('.classic-facts')
    expect(facts.text()).toContain('Device type: Operating System')
    expect(facts.text()).toContain('Created by: PVWA')
    expect(facts.text()).toContain('Logon account: Logon Safe\\logon-acct')
    expect(facts.text()).toContain('Reconcile account: Rec Safe\\rec-acct')
    const links = facts.findAll('a')
    expect(links).toHaveLength(1)
    expect(links[0].text()).toBe('Rec Safe\\rec-acct')
    expect(links[0].attributes('href')).toBe('/accounts/77')
  })
})
