import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import DnaAccounts from './DnaAccounts.vue'

// D-325: the DNA accounts report: findings, tracked or not, and the tracked account linked.
function jsonResponse(body, { total = null, filtered = total } = {}) {
  return {
    ok: true,
    status: 200,
    headers: { get: (name) => (name === 'X-Total-Count' ? (total === null ? null : String(total)) : name === 'X-Filtered-Count' ? (filtered === null ? null : String(filtered)) : null) },
    json: () => Promise.resolve(body)
  }
}

const ROWS = [
  { dnaAccountKey: 1, sheetCode: 'NC-DA', machineName: 'SRV01', accountName: 'svc_one', displayName: 'Service One', accountCategory: 'Privileged', privilegedDomainGroup: 'Domain Admins', accountState: 'Enabled', passwordAgeDays: 400, lastLoginDate: null, trackedAccountKey: 7, trackedAccountName: 'svc_one' },
  { dnaAccountKey: 2, sheetCode: 'NC-LA', machineName: 'SRV02', accountName: 'localadmin', displayName: null, accountCategory: 'Local', accountGroup: 'Administrators', accountState: 'Enabled', passwordAgeDays: null, lastLoginDate: null, trackedAccountKey: null }
]

describe('DnaAccounts.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists the accounts with their finding, links a tracked one, and filters by finding and tracked', async () => {
    globalThis.fetch = vi.fn((url) => Promise.resolve(url === '/api/reports/dna-accounts/sheets' ? jsonResponse(['NC-DA', 'NC-LA']) : jsonResponse(ROWS, { total: 2 })))
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }, { path: '/accounts/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }] })
    const wrapper = mount(DnaAccounts, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    expect(wrapper.find('tbody a').attributes('href')).toBe('/accounts/7')
    expect(wrapper.text()).toContain('400 days')
    expect(wrapper.findAll('tbody tr')[1].text()).toContain('No')

    const selects = wrapper.findAll('select')
    await selects[0].setValue('NC-LA')
    await selects[1].setValue('false')
    await flushPromises()
    const last = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/dna-accounts?')).at(-1)
    expect(last).toContain('sheet=NC-LA')
    expect(last).toContain('tracked=false')
  })
})
