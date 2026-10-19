import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import DataSources from './DataSources.vue'
import { feedTypeLabel, formatRunTime } from './dataFeedFormat'

// D-181: Run now asks first inside business hours (the shared dialog is
// mocked, as in the other admin page tests).
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/data-sources', name: 'admin-data-sources', component: { template: '<div />' } },
      { path: '/admin/data-sources/new', name: 'admin-data-feed-create', component: { template: '<div />' } },
      { path: '/admin/data-sources/:dataFeedKey', name: 'admin-data-feed-edit', component: { template: '<div />' } }
    ]
  })
}

const feed = {
  dataFeedKey: 7,
  displayName: 'Nightly safes',
  feedType: 'SafeAssignments',
  folderPath: 'D:\\Feeds',
  fileNamePattern: 'safes_{yyyy-MM-dd}.csv',
  importMappingProfileName: null,
  isEnabled: true,
  displayOrder: 1,
  lastRun: { outcome: 'CompletedWithErrors', startedAt: '2026-10-09T04:00:00', summary: '3 assigned, 1 errors' }
}

// D-211: the CyberArk sources' settings, as GET /api/admin/cyberark-sources returns them.
function cyberArk(overrides = {}) {
  return {
    isConfigured: true,
    settings: { importPrivilegeCloud: true, exportFolderPath: 'D:\\Exports', importSelfHosted: false, evdDatabaseName: null },
    ...overrides
  }
}

function status(overrides = {}) {
  return { isRunning: false, runningFeed: null, withinBusinessHours: false, nextScheduledRun: '2026-10-10T04:00:00', ...overrides }
}

// Answers by URL, so the order of the page's parallel loads doesn't matter.
function routeFetch(handlers) {
  globalThis.fetch = vi.fn((url, options) => {
    const key = `${options?.method ?? 'GET'} ${url}`
    const handler = handlers[key]
    if (!handler) throw new Error(`Unexpected fetch: ${key}`)
    return Promise.resolve(typeof handler === 'function' ? handler() : handler)
  })
}

describe('DataSources.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists feeds with their last run and the next nightly run', async () => {
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([feed]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk())
    })

    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Nightly safes')
    expect(wrapper.text()).toContain('Safe → application assignments')
    expect(wrapper.text()).toContain('CompletedWithErrors')
    expect(wrapper.text()).toContain('3 assigned, 1 errors')
    expect(wrapper.text()).toContain('Next nightly run')
    expect(wrapper.text()).toContain('Privilege Cloud: on, from D:\\Exports')
    expect(wrapper.text()).toContain('Self-Hosted: off')
  })

  it('warns when the CyberArk sources are not set up (D-211)', async () => {
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk({ isConfigured: false }))
    })

    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Not set up yet')
  })

  it('Run now outside business hours starts the run without asking', async () => {
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([feed]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk()),
      'POST /api/admin/data-feeds/run': jsonResponse(null, true, 202)
    })
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Run now').trigger('click')
    await flushPromises()

    expect(confirmDelete).not.toHaveBeenCalled()
    const runCall = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/data-feeds/run')
    expect(JSON.parse(runCall[1].body)).toEqual({ dataFeedKeys: [7] })
    expect(wrapper.text()).toContain('Started: "Nightly safes"')
    wrapper.unmount()
  })

  it('Run all inside business hours asks first, and does nothing if cancelled', async () => {
    confirmDelete.mockResolvedValue(false)
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([feed]),
      'GET /api/admin/data-feeds/status': jsonResponse(status({ withinBusinessHours: true })),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk())
    })
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Run all').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('business hours'), 'Run now')
    expect(globalThis.fetch.mock.calls.some(([url]) => url === '/api/admin/data-feeds/run')).toBe(false)
  })

  it('shows a message when a run is already in progress (409)', async () => {
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([feed]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk()),
      'POST /api/admin/data-feeds/run': jsonResponse({ detail: 'Running: Other feed' }, false, 409)
    })
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Run now').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('already in progress')
    expect(wrapper.find('[role="alert"]').text()).toContain('Other feed')
    wrapper.unmount()
  })

  // D-248: Self-Hosted Direct.
  const directSettings = {
    importPrivilegeCloud: false, exportFolderPath: null, importSelfHosted: true, evdDatabaseName: null,
    selfHostedSource: 'Direct', pvwaBaseUrl: 'https://pvwa.example.com', pvwaCredentialKey: 3, pvwaLogonMethod: 'CyberArk',
    directPullTime: '01:30', directMaxChangePercent: 20
  }
  const pull = (overrides = {}) => ({
    pullKey: 5, startedAt: '2026-10-19T07:30:00', completedAt: '2026-10-19T07:30:09', status: 'Succeeded', triggeredBy: 'schedule',
    acceptedChange: false, safeCount: 40, accountCount: 2452, userCount: 43, groupCount: 21, memberCount: 88, platformCount: 20, message: null,
    ...overrides
  })
  function directHandlers(pulls, running = false, extra = {}) {
    return {
      'GET /api/admin/data-feeds': jsonResponse([]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk({ settings: directSettings })),
      'GET /api/admin/cyberark-sources/direct/pulls': jsonResponse({ running, pulls }),
      ...extra
    }
  }

  it('summarizes Self-Hosted Direct and lists the recent pulls with their counts', async () => {
    routeFetch(directHandlers([pull(), pull({ pullKey: 4, status: 'Rejected', message: 'Safes went from 40 to 10', safeCount: 10, accountCount: 100 })]))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Self-Hosted: on, read from PVWA at https://pvwa.example.com every night at 01:30')
    expect(wrapper.text()).toContain('Self-Hosted Direct pulls')
    expect(wrapper.text()).toContain('40 Safes, 2452 accounts, 43 users, 21 groups')
    expect(wrapper.find('.outcome-Rejected').text()).toBe('Rejected')
    expect(wrapper.text()).toContain('Safes went from 40 to 10')
  })

  it('does not load the pull log for the EVD source', async () => {
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
      'GET /api/admin/cyberark-sources': jsonResponse(cyberArk({ settings: { importPrivilegeCloud: false, importSelfHosted: true, selfHostedSource: 'Evd', evdDatabaseName: 'CyberArkSH' } }))
    })
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Self-Hosted: on, from the CyberArkSH database')
    expect(wrapper.text()).not.toContain('Self-Hosted Direct pulls')
  })

  it('Pull now starts a pull without asking outside business hours', async () => {
    const post = vi.fn(() => jsonResponse(null, true, 202))
    routeFetch(directHandlers([pull()], false, { 'POST /api/admin/cyberark-sources/direct/pull': post }))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Pull now').trigger('click')
    await flushPromises()

    expect(confirmDelete).not.toHaveBeenCalled()
    const call = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/cyberark-sources/direct/pull')
    expect(JSON.parse(call[1].body)).toEqual({ acceptChange: false })
    expect(wrapper.text()).toContain('Pull started')
  })

  it('offers Accept the change and pull after a rejected pull, and always asks first', async () => {
    const post = vi.fn(() => jsonResponse(null, true, 202))
    routeFetch(directHandlers([pull({ status: 'Rejected', message: 'Safes went from 40 to 10' })], false, { 'POST /api/admin/cyberark-sources/direct/pull': post }))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Accept the change and pull').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledTimes(1)
    const call = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/cyberark-sources/direct/pull')
    expect(JSON.parse(call[1].body)).toEqual({ acceptChange: true })
  })

  it('does not start a pull when the confirmation is declined', async () => {
    confirmDelete.mockResolvedValue(false)
    routeFetch(directHandlers([pull({ status: 'Rejected' })]))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Accept the change and pull').trigger('click')
    await flushPromises()

    expect(globalThis.fetch.mock.calls.some(([url]) => url === '/api/admin/cyberark-sources/direct/pull')).toBe(false)
  })

  it('has no Accept button after a good pull, and disables Pull now while one runs', async () => {
    routeFetch(directHandlers([pull()], true))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('A pull is running')
    expect(wrapper.findAll('button').some(b => b.text() === 'Accept the change and pull')).toBe(false)
    expect(wrapper.findAll('button').find(b => b.text() === 'Pull now').attributes('disabled')).toBeDefined()
    wrapper.unmount()   // stops the polling timer
  })

  it('shows the API\'s reason when a pull is refused', async () => {
    routeFetch(directHandlers([pull()], false, {
      'POST /api/admin/cyberark-sources/direct/pull': jsonResponse({ detail: 'Save Self-Hosted Direct as the source first.' }, false, 400)
    }))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Pull now').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Save Self-Hosted Direct as the source first.')
  })

  it('Details opens a dialog with the whole message, the counts and what to do, and closes again', async () => {
    routeFetch(directHandlers([pull({ status: 'Rejected', message: 'Safes went from 40 to 10 (-30; at most 8 allowed at 20%) | User \'x\': details not read' })]))
    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] }, attachTo: document.body })
    await flushPromises()
    expect(document.body.querySelector('[role="dialog"]')).toBeNull()

    await wrapper.findAll('button').find(b => b.text() === 'Details').trigger('click')
    await flushPromises()

    const dialog = document.body.querySelector('[role="dialog"]')
    expect(dialog.textContent).toContain('Self-Hosted Direct pull')
    expect(dialog.textContent).toContain('Safes went from 40 to 10')
    expect(dialog.textContent).toContain("User 'x': details not read")
    expect(dialog.querySelectorAll('.pull-details-messages li')).toHaveLength(2)
    expect(dialog.textContent).toContain('2452')
    expect(dialog.textContent).toContain('Accept the change and pull')

    ;[...dialog.querySelectorAll('button')].find(b => b.textContent === 'Close').click()
    await flushPromises()
    expect(document.body.querySelector('[role="dialog"]')).toBeNull()
    wrapper.unmount()
  })
})

describe('dataFeedFormat', () => {
  it('labels feed types, falling back to the raw value', () => {
    expect(feedTypeLabel('TargetInventory')).toBe('Target inventory')
    expect(feedTypeLabel('Unknown')).toBe('Unknown')
  })

  it('treats run times without a zone as UTC, and local times as local', () => {
    expect(formatRunTime('2026-10-09T04:00:00')).toBe(new Date('2026-10-09T04:00:00Z').toLocaleString())
    expect(formatRunTime('2026-10-09T04:00:00', true)).toBe(new Date('2026-10-09T04:00:00').toLocaleString())
    expect(formatRunTime(null)).toBe('')
  })
})
