import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import DnaAccounts from './DnaAccounts.vue'
import { useRightsStore } from '../../stores/rights'

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

  it('lists the findings alphabetically after All', async () => {
    globalThis.fetch = vi.fn((url) => Promise.resolve(url === '/api/reports/dna-accounts/sheets' ? jsonResponse(['NC-LA', 'nc-bb', 'NC-DA']) : jsonResponse([], { total: 0 })))
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }] })
    const wrapper = mount(DnaAccounts, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.findAll('select')[0].findAll('option').map(o => o.element.value)).toEqual(['', 'nc-bb', 'NC-DA', 'NC-LA'])
  })

  // D-341: click a heading to sort; D-344: hidden columns are named; D-342: Track in BlueTrack.
  describe('sorting, hidden columns and tracking', () => {
    const router = () => createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }, { path: '/accounts/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }] })
    const listCalls = () => globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/dna-accounts?'))

    function respond({ hidden = '', track = null } = {}) {
      globalThis.fetch = vi.fn((url, init) => {
        if (url === '/api/reports/dna-accounts/sheets') return Promise.resolve(jsonResponse(['NC-DA']))
        if (url === '/api/reports/dna-accounts/track') return Promise.resolve(track ?? jsonResponse({ tracked: JSON.parse(init.body).dnaAccountKeys.length }))
        const response = jsonResponse(ROWS, { total: 2 })
        const get = response.headers.get
        response.headers.get = name => (name === 'X-Hidden-Columns' ? hidden : get(name))
        return Promise.resolve(response)
      })
    }

    it('sorts by a heading, and the other way on a second click', async () => {
      respond()
      const wrapper = mount(DnaAccounts, { global: { plugins: [router()] } })
      await flushPromises()

      const heading = () => wrapper.findAll('th').find(th => th.text().startsWith('Last login'))
      await heading().find('button').trigger('click')
      await flushPromises()
      expect(new URLSearchParams(listCalls().at(-1).split('?')[1]).get('sort')).toBe('LastLoginDate:desc')
      expect(heading().attributes('aria-sort')).toBe('descending')

      await heading().find('button').trigger('click')
      await flushPromises()
      expect(new URLSearchParams(listCalls().at(-1).split('?')[1]).get('sort')).toBe('LastLoginDate:asc')
    })

    it('says which columns are hidden, and a hidden one is not sortable', async () => {
      respond({ hidden: 'MachineName,OsVersion' })
      const wrapper = mount(DnaAccounts, { global: { plugins: [router()] } })
      await flushPromises()

      expect(wrapper.find('.hidden-note').text()).toBe('2 columns are hidden: they need the ViewDnaSensitiveColumns permission.')
      expect(wrapper.findAll('th').find(th => th.text().startsWith('Machine')).find('button').exists()).toBe(false)
    })

    it('tracks the chosen untracked accounts, for someone who may', async () => {
      respond()
      useRightsStore().permissionNames = ['ManageDiscoveredAccounts']
      const wrapper = mount(DnaAccounts, { global: { plugins: [router()] } })
      await flushPromises()

      const boxes = wrapper.findAll('tbody input[type="checkbox"]')
      expect(boxes).toHaveLength(1)   // the tracked one has no box
      await boxes[0].setValue(true)
      await wrapper.findAll('button').find(b => b.text().startsWith('Track in BlueTrack')).trigger('click')
      await flushPromises()

      const post = globalThis.fetch.mock.calls.find(([url]) => url === '/api/reports/dna-accounts/track')
      expect(JSON.parse(post[1].body)).toEqual({ dnaAccountKeys: [2] })
      expect(wrapper.text()).toContain('1 account is now tracked in BlueTrack.')
    })

    // D-347: an account DNA couldn't resolve is marked, and can be shown on its own.
    it('marks an unresolved SID, and filters to them', async () => {
      globalThis.fetch = vi.fn((url) => Promise.resolve(url === '/api/reports/dna-accounts/sheets'
        ? jsonResponse(['NC-DA'])
        : jsonResponse([{ ...ROWS[1], dnaAccountKey: 3, accountName: 'S-1-5-21-1-2-3-1105', isUnresolvedSid: true }], { total: 1 })))
      const wrapper = mount(DnaAccounts, { global: { plugins: [router()] } })
      await flushPromises()

      expect(wrapper.find('.unresolved-sid').text()).toContain("Unresolved SID: DNA couldn't name this account")
      await wrapper.find('.checkbox-filter input').setValue(true)
      await flushPromises()
      expect(new URLSearchParams(listCalls().at(-1).split('?')[1]).get('unresolvedSid')).toBe('true')
    })

    it('offers no tracking without ManageDiscoveredAccounts', async () => {
      respond()
      const wrapper = mount(DnaAccounts, { global: { plugins: [router()] } })
      await flushPromises()
      expect(wrapper.findAll('table input[type="checkbox"]')).toHaveLength(0)
      expect(wrapper.text()).not.toContain('Track in BlueTrack')
    })
  })
})
