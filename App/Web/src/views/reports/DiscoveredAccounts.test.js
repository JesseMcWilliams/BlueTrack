import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import DiscoveredAccounts from './DiscoveredAccounts.vue'

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
  globalThis.fetch = vi.fn((url) => {
    if (url.startsWith('/api/reports/discovered-accounts/') && url.endsWith('/access-groups')) {
      return Promise.resolve(jsonResponse([{ accessGroupKey: 1, groupName: 'CyberArk Vault Admins' }]))
    }
    if (url.startsWith('/api/reports/discovered-accounts')) return Promise.resolve(jsonResponse(rows, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('DiscoveredAccounts.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
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
})
