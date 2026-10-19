import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import DecomAccounts from './DecomAccounts.vue'
import DecomSafes from './DecomSafes.vue'
import OverdueAtRiskWorklist from './OverdueAtRiskWorklist.vue'
import { useRightsStore } from '../../stores/rights'

// UI review W6-1, W6-2, W6-4 and W7-4: search, sort, count and paging in the decommission and Overdue/At-Risk
// reports, the Safe filter and links between the two decommission reports, and days overdue.
const stub = { template: '<div />' }
function json(body) { return { ok: true, status: 200, json: () => Promise.resolve(body) } }

async function mountAt(component, body, query = {}) {
  globalThis.fetch = vi.fn(() => Promise.resolve(json(body)))
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/page', name: 'page', component },
      { path: '/accounts-report', name: 'reports-decom-accounts', component: stub },
      { path: '/safes-report', name: 'reports-decom-safes', component: stub },
      { path: '/app/:applicationKey', name: 'admin-application-edit', component: stub },
      { path: '/a/:accountKey', name: 'account-progress-detail', component: stub }
    ]
  })
  router.push({ name: 'page', query })
  await router.isReady()
  const wrapper = mount(component, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

const account = (n, extra = {}) => ({ accountKey: n, accountName: `acct${n}`, userName: `user${n}`, address: `h${n}.example.com`, isDeleted: false, safeName: 'SafeA', sourceSystemName: 'Self-Hosted', inFlaggedSafe: true, nameFlagged: false, otherSafes: null, ...extra })

describe('report tables', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('Accounts Flagged for Deletion: searches, sorts, counts, and keeps them in the address', async () => {
    const rows = [account(1, { userName: 'zed' }), account(2, { userName: 'amy', safeName: 'SafeB' }), account(3, { userName: 'bob', isDeleted: true })]
    const { wrapper, router } = await mountAt(DecomAccounts, rows)
    expect(wrapper.text()).toContain('Showing 1–3 of 3')

    await wrapper.find('input[type="search"]').setValue('safeb')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('1 matching')

    await wrapper.find('input[type="search"]').setValue('')
    await wrapper.findAll('th button').find(b => b.text().startsWith('Username')).trigger('click')
    expect(wrapper.findAll('tbody tr').map(r => r.find('a').text())).toEqual(['amy', 'bob', 'zed'])
    await flushPromises()
    expect(router.currentRoute.value.query.sort).toBe('userName:asc')
  })

  it('Accounts Flagged for Deletion: ?safe= shows one Safe, and the chip clears it', async () => {
    const rows = [account(1), account(2, { safeName: 'SafeB' })]
    const { wrapper } = await mountAt(DecomAccounts, rows, { safe: 'SafeB' })
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.find('.safe-filter').text()).toContain('Only Safe SafeB.')

    await wrapper.find('.safe-filter button').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
  })

  it('Safes Flagged for Deletion: the count opens that Safe in the Accounts report, and the application links for someone who can edit it', async () => {
    useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames: ['CurateApplicationMapping'] })
    const rows = [{ safeKey: 1, safeName: 'Old_Safe', sourceSystemName: 'Self-Hosted', applicationName: 'Payroll', applicationKey: 7, activeAccountCount: 4, deletedAccountCount: 1 },
      { safeKey: 2, safeName: 'Empty_Safe', sourceSystemName: 'Self-Hosted', applicationName: null, applicationKey: null, activeAccountCount: 0, deletedAccountCount: 0 }]
    const { wrapper } = await mountAt(DecomSafes, rows)
    const links = wrapper.findAll('tbody a').map(a => `${a.text()}=${a.attributes('href')}`)
    expect(links).toEqual(['Payroll=/app/7', '4=/accounts-report?safe=Old_Safe'])

    useRightsStore().$patch({ permissionNames: [] })
    await flushPromises()
    expect(wrapper.findAll('tbody a').map(a => a.text())).toEqual(['4'])
  })

  it('Overdue/At-Risk: shows days overdue, sorts by it, and searches', async () => {
    const day = 86400000
    const rows = [
      { accountKey: 1, accountName: 'recent', stageName: 'Managed', statusName: 'In Progress', riskLevelName: 'High', ownerName: 'Pat', targetRemediationDate: new Date(Date.now() - 3 * day - 3600000).toISOString() },
      { accountKey: 2, accountName: 'ancient', stageName: 'Discovery', statusName: 'Blocked', riskLevelName: 'Low', ownerName: 'Sam', targetRemediationDate: new Date(Date.now() - 40 * day - 3600000).toISOString() }
    ]
    const { wrapper } = await mountAt(OverdueAtRiskWorklist, rows)
    expect(wrapper.find('th button').text()).toContain('Account')
    const daysCell = row => row.findAll('td').at(-1).text()
    expect(wrapper.findAll('tbody tr').map(daysCell)).toEqual(['3', '40'])

    const header = wrapper.findAll('th button').find(b => b.text().startsWith('Days overdue'))
    await header.trigger('click')
    await header.trigger('click')
    expect(wrapper.findAll('tbody tr').map(daysCell)).toEqual(['40', '3'])

    await wrapper.find('input[type="search"]').setValue('sam')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    await wrapper.find('input[type="search"]').setValue('nobody')
    expect(wrapper.text()).toContain('No accounts match the search.')
  })
})
