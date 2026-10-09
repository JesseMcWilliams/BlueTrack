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
      'GET /api/admin/data-feeds/status': jsonResponse(status())
    })

    const wrapper = mount(DataSources, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Nightly safes')
    expect(wrapper.text()).toContain('Safe → application assignments')
    expect(wrapper.text()).toContain('CompletedWithErrors')
    expect(wrapper.text()).toContain('3 assigned, 1 errors')
    expect(wrapper.text()).toContain('Next nightly run')
  })

  it('Run now outside business hours starts the run without asking', async () => {
    routeFetch({
      'GET /api/admin/data-feeds': jsonResponse([feed]),
      'GET /api/admin/data-feeds/status': jsonResponse(status()),
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
      'GET /api/admin/data-feeds/status': jsonResponse(status({ withinBusinessHours: true }))
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
