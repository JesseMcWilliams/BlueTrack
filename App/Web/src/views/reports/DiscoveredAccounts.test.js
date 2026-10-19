import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
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

  // UI review W5-6 and W5-7
  it('links the possible-existing account, and the name toggle says whether its panel is open', async () => {
    mockInitialLoad({ rows: [{ ...sampleRow, possibleExistingAccountKey: 42, possibleExistingAccountName: 'svc-web01' }] })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/a/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }] })
    const wrapper = mount(DiscoveredAccounts, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.find('.possible-match a').attributes('href')).toBe('/a/42')
    const toggle = wrapper.find('button.link-button')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    await flushPromises()
    expect(wrapper.find('button.link-button').attributes('aria-expanded')).toBe('true')
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

  it('Dismiss asks first, then posts and reloads the list', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    confirmDelete.mockResolvedValue(true)
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const dismissButton = wrapper.findAll('button').find(b => b.text() === 'Dismiss')
    await dismissButton.trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('Dismiss "'), 'Dismiss')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/discovered-accounts/1/dismiss', { method: 'POST' })
  })

  // UI review W5-4
  it('Show dismissed lists the dismissed accounts with Restore, and Restore posts and reloads', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    await wrapper.find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    const listUrl = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/discovered-accounts?')).at(-1)
    expect(listUrl).toContain('dismissed=true')
    expect(wrapper.findAll('button').map(b => b.text()).filter(t => ['Accept', 'Dismiss', 'Restore'].includes(t))).toEqual(['Restore'])

    await wrapper.findAll('button').find(b => b.text() === 'Restore').trigger('click')
    await flushPromises()
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/discovered-accounts/1/restore', { method: 'POST' })
  })

  it('Dismiss does nothing when the confirmation is cancelled', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    confirmDelete.mockResolvedValue(false)
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Dismiss').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).not.toHaveBeenCalledWith('/api/reports/discovered-accounts/1/dismiss', { method: 'POST' })
  })

  // D-233: account classification columns, Class filter and the per-row override.
  const classifiedRow = {
    ...sampleRow,
    samAccountName: 'adm.jsmith',
    accountClass: 'Admin',
    classSource: 'Rule',
    classRuleKey: 7,
    classRuleType: 'Name',
    classRuleAttributeName: 'sAMAccountName',
    classRuleOperator: 'StartsWith',
    classRuleValue: 'adm.',
    ownerSamAccountName: 'jsmith',
    ownerDisplayName: 'John Smith',
    ownerSource: 'Name',
    classOverride: null,
    ownerOverrideSamAccountName: null
  }

  it('shows Account Class, Owner and Matched by', async () => {
    mockInitialLoad({ rows: [classifiedRow] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const headers = wrapper.findAll('th').map(th => th.text())
    expect(headers).toEqual(expect.arrayContaining([expect.stringContaining('Account Class'), expect.stringContaining('Owner'), 'Matched by']))
    const cells = wrapper.findAll('tbody tr')[0].findAll('td').map(td => td.text())
    expect(cells[2]).toBe('Admin')
    expect(cells[3]).toBe('jsmith (John Smith)')
    // "Matched by" is the last column, as its heading is (UI review W5-1).
    expect(cells[cells.length - 1]).toContain('sAMAccountName starts with "adm."')
    expect(cells[cells.length - 1]).toContain('Owner by name')
  })

  it('shows Ambiguous, the default and a manual class in words', async () => {
    mockInitialLoad({ rows: [
      { ...sampleRow, discoveredAccountKey: 2, accountClass: 'Secondary', classSource: 'Rule', classRuleType: 'OU', classRuleOperator: 'Contains', classRuleValue: 'OU=Admins', ownerSource: 'Ambiguous' },
      { ...sampleRow, discoveredAccountKey: 3, accountClass: 'Unknown', classSource: 'Default' },
      { ...sampleRow, discoveredAccountKey: 4, accountClass: 'Primary', classSource: 'Manual', overrideByName: 'Pat Admin', ownerSamAccountName: 'jsmith', ownerSource: 'Manual' }
    ] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('distinguished name contains "OU=Admins"')
    expect(text).toContain('Owner ambiguous')
    expect(text).toContain('No rule matched')
    expect(text).toContain('Set by Pat Admin')
  })

  it('the Class filter reloads page 1 with accountClass and passes it to the export', async () => {
    mockInitialLoad({ rows: [classifiedRow] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    await wrapper.get('select').setValue('Admin')
    await flushPromises()

    const last = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/discovered-accounts?')).at(-1)
    expect(last).toContain('accountClass=Admin')
    expect(last).toContain('page=1')

    await wrapper.findAll('button').find(b => b.text() === 'Export CSV').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.map(c => c[0]).some(u => u.startsWith('/api/reports/discovered-accounts/export?') && u.includes('accountClass=Admin'))).toBe(true)
  })

  it('has no Classify button without ManageDiscoveredAccounts', async () => {
    mockInitialLoad({ rows: [classifiedRow] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    expect(wrapper.findAll('button').some(b => b.text() === 'Classify')).toBe(false)
  })

  it('Classify saves a class/owner override, then reloads', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    mockInitialLoad({ rows: [classifiedRow] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    await wrapper.get('button[aria-label="Classify adm.jsmith"]').trigger('click')
    const form = wrapper.get('form.override-form')
    await form.get('select').setValue('Secondary')
    await form.get('input').setValue('  jsmith ')
    await form.trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(c => c[0] === '/api/reports/discovered-accounts/1/classification')
    expect(put[1].method).toBe('PUT')
    expect(JSON.parse(put[1].body)).toEqual({ accountClass: 'Secondary', ownerSamAccountName: 'jsmith' })
    expect(wrapper.find('form.override-form').exists()).toBe(false)
  })

  it('Clear Override sends nulls for both', async () => {
    const rights = useRightsStore()
    rights.permissionNames = ['ManageDiscoveredAccounts']
    mockInitialLoad({ rows: [{ ...classifiedRow, classOverride: 'Primary', classSource: 'Manual' }] })
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    await wrapper.get('button[aria-label="Classify adm.jsmith"]').trigger('click')
    expect(wrapper.get('form.override-form select').element.value).toBe('Primary')
    await wrapper.findAll('button').find(b => b.text() === 'Clear Override').trigger('click')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(c => c[0] === '/api/reports/discovered-accounts/1/classification')
    expect(JSON.parse(put[1].body)).toEqual({ accountClass: null, ownerSamAccountName: null })
  })

  // UI review W7-2: the sort and page are in the page address, so a view can be shared and reloaded.
  describe('address', () => {
    async function mountAt(path) {
      const page = { template: '<div />' }
      const router = createRouter({
        history: createMemoryHistory(),
        routes: [{ path: '/', name: 'home', component: page }, { path: '/report', name: 'report', component: page }]
      })
      await router.push(path)
      const wrapper = mount(DiscoveredAccounts, { global: { plugins: [router] } })
      await flushPromises()
      return { wrapper, router }
    }

    const calls = () => globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/discovered-accounts?'))
    const lastCall = () => new URLSearchParams(calls().at(-1).split('?')[1])

    it('reads the sort and page from the address into the request', async () => {
      mockInitialLoad({ totalCount: 500 })
      await mountAt('/report?sort=samAccountName:desc&page=2')

      expect(lastCall().get('sort')).toBe('samAccountName:desc')
      expect(lastCall().get('page')).toBe('2')
    })

    it('ignores a sort field the page does not have, and a bad page', async () => {
      mockInitialLoad()
      await mountAt('/report?sort=bogus:asc&page=0')

      expect(lastCall().get('sort')).toBeNull()
      expect(lastCall().get('page')).toBe('1')
    })

    it('a sort click is in the address, and so is a later page', async () => {
      mockInitialLoad({ totalCount: 500 })
      const { wrapper, router } = await mountAt('/report')

      await wrapper.findAll('th').find(th => th.text().startsWith('Account')).find('button').trigger('click')
      await flushPromises()
      expect(router.currentRoute.value.query.sort).toContain('samAccountName:')

      await wrapper.findAll('.pager button').find(b => b.text().includes('Next')).trigger('click')
      await flushPromises()
      expect(router.currentRoute.value.query.page).toBe('2')
    })

    it('follows the address when it changes under the page, with one reload', async () => {
      mockInitialLoad({ totalCount: 500 })
      const { router } = await mountAt('/report')
      const before = calls().length

      await router.push('/report?page=3')
      await flushPromises()

      expect(calls().length).toBe(before + 1)
      expect(lastCall().get('page')).toBe('3')
    })

    it('reads the class filter from the address', async () => {
      const { wrapper } = await mountAt('/report?class=Admin')

      expect(lastCall().get('accountClass')).toBe('Admin')
      expect(wrapper.find('select').element.value).toBe('Admin')
    })

    it('ignores a class the page does not have', async () => {
      await mountAt('/report?class=Everything')

      expect(lastCall().get('accountClass')).toBeNull()
    })

    it('a class chosen in the dropdown is in the address, and the page goes back to 1', async () => {
      const { wrapper, router } = await mountAt('/report?page=3')
      expect(router.currentRoute.value.query).toEqual({ page: '3' })

      await wrapper.find('select').setValue('Admin')
      await flushPromises()

      expect(router.currentRoute.value.query).toEqual({ class: 'Admin' })
    })
  })

  // UI review W5-1: "Matched by" sat in the fifth body column but is the last header, so every value from there on
  // was under the wrong heading.
  it('puts each value under its own column heading', async () => {
    mockInitialLoad()
    const wrapper = mount(DiscoveredAccounts)
    await flushPromises()

    const headers = wrapper.findAll('thead th').map(th => th.text().replace(/[▲▼↑↓]/g, '').trim())
    const cells = wrapper.findAll('tbody tr')[0].findAll('td').map(td => td.text())
    expect(cells).toHaveLength(headers.length)
    expect(cells[headers.indexOf('Risk Score')]).toBe('400')
    expect(cells[headers.indexOf('Risk Band')]).toBe('Medium')
    expect(headers[headers.length - 1]).toBe('Matched by')
    expect(cells[headers.indexOf('Account')]).toContain('jsmith')
    expect(cells[headers.indexOf('Domain')]).toBe('company.local')
  })
})
