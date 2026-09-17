import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useRightsStore } from '../../stores/rights'
import DiscoveredAccounts from './DiscoveredAccounts.vue'

// D-128: confirmDelete is this app's one generic confirm-before-a-real-write
// dialog (Accept reuses it here) -- mocked per AccessGroups.test.js/
// Targets.test.js's own established pattern. Re-established in beforeEach,
// not just the factory here, since a file-level afterEach(vi.restoreAllMocks())
// silently clears a factory-level mockResolvedValue after the first test uses
// it (a known Vitest footgun hit more than once in this project already).
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

// AD Account Discovery feature (2026-09-16) -- mirrors RiskScoreReport.test.js's
// own mocking style (same report shape: sortable/paginated list + per-row
// drill-down).
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
  discoveredAccountKey: 1,
  domainName: 'company.local',
  samAccountName: 'jsmith',
  displayName: 'John Smith',
  isEnabled: true,
  computedRiskScore: 400,
  riskScoreBandName: 'Medium',
  discoveredDate: '2026-09-01T00:00:00Z',
  lastSeenDate: '2026-09-15T00:00:00Z',
  possibleExistingAccountKey: null,
  possibleExistingAccountName: null
}

function mockInitialLoad({ rows = [sampleRow], totalCount = rows.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url, options) => {
    if (url.startsWith('/api/reports/discovered-accounts/') && url.endsWith('/access-groups')) {
      return Promise.resolve(jsonResponse([{ accessGroupKey: 1, groupName: 'CyberArk Vault Admins' }]))
    }
    if (url.endsWith('/accept') && options?.method === 'POST') return Promise.resolve(jsonResponse({ accountKey: 999 }))
    if (url.endsWith('/dismiss') && options?.method === 'POST') return Promise.resolve(jsonResponse(null))
    if (url.startsWith('/api/reports/discovered-accounts')) return Promise.resolve(jsonResponse(rows, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('DiscoveredAccounts.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the report', async () => {
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    expect(wrapper.text()).toContain('jsmith')
    expect(wrapper.text()).toContain('John Smith')
  })

  it('shows the possible-existing-account flag when set', async () => {
    mockInitialLoad({ rows: [{ ...sampleRow, possibleExistingAccountKey: 42, possibleExistingAccountName: 'svc-web01' }] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    expect(wrapper.text()).toContain('Possibly already onboarded as "svc-web01"')
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ rows: [sampleRow], totalCount: 30, filteredCount: 30 })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 30 matching (30 total)')
  })

  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ rows: [] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/discovered-accounts?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('clicking the account name expands the matched Access Groups drill-down', async () => {
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    await wrapper.get('button.link-button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('CyberArk Vault Admins')
  })

  it('does not show Accept/Dismiss without ManageDiscoveredAccounts', async () => {
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    expect(wrapper.findAll('button').some(b => b.text() === 'Accept')).toBe(false)
    expect(wrapper.findAll('button').some(b => b.text() === 'Dismiss')).toBe(false)
  })

  it('Accept confirms via the shared dialog, then posts and reloads the list', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const acceptButton = wrapper.findAll('button').find(b => b.text() === 'Accept')
    await acceptButton.trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('Accept "jsmith"'), 'Accept')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/discovered-accounts/1/accept', { method: 'POST' })
    // load() re-fetches the list on success
    expect(globalThis.fetch.mock.calls.filter(c => c[0].startsWith('/api/reports/discovered-accounts?')).length).toBeGreaterThan(1)
  })

  it('Accept does nothing if the confirm dialog is declined', async () => {
    confirmDelete.mockResolvedValueOnce(false)
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const acceptButton = wrapper.findAll('button').find(b => b.text() === 'Accept')
    await acceptButton.trigger('click')
    await flushPromises()

    expect(globalThis.fetch).not.toHaveBeenCalledWith('/api/reports/discovered-accounts/1/accept', { method: 'POST' })
  })

  it('Dismiss posts without a confirm prompt, then reloads the list', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const dismissButton = wrapper.findAll('button').find(b => b.text() === 'Dismiss')
    await dismissButton.trigger('click')
    await flushPromises()

    expect(confirmDelete).not.toHaveBeenCalled()
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/discovered-accounts/1/dismiss', { method: 'POST' })
  })
})
