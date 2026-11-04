import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import Home from './Home.vue'
import { useRightsStore } from '../stores/rights'

// D-307: the landing page offers only the areas the user may open, and the KPI funnel only to those who may see tracked data.
const stub = { template: '<div />' }
const NAMES = ['dashboard', 'account-progress-list', 'risk-exceptions-list', 'access-groups', 'targets', 'reports', 'admin', 'my-profile',
  'reports-overdue-worklist', 'risk-exceptions-overdue-worklist', 'risk-exceptions-approval-worklist']

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', name: 'home', component: stub }, ...NAMES.map(name => ({ path: `/${name}`, name, component: stub }))]
  })
}

const KPIS = { totalAccounts: 200, inScopeAccounts: 150, onboardedAccounts: 120, managedAccounts: 60, compliantAccounts: 30, archivedAccounts: 10, discoveredAccounts: 250 }

async function mountHome(permissionNames, roleNames = ['Viewer']) {
  useRightsStore().$patch({ loaded: true, authenticated: true, displayName: 'Jane Doe', permissionNames, roleNames })
  const wrapper = mount(Home, { global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

const cards = wrapper => wrapper.findAll('.home-area h3').map(h => h.text())

describe('Home.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(KPIS) }))
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('greets the user by name and names their roles', async () => {
    const wrapper = await mountHome(['ViewDashboard'], ['Viewer', 'Analyst'])

    expect(wrapper.find('h1').text()).toBe('Welcome, Jane Doe')
    expect(wrapper.text()).toContain('Your roles: Viewer, Analyst.')
  })

  it('puts what needs attention on its card, linking to the worklist, and only counts above zero (D-330)', async () => {
    const lists = {
      '/api/reports/overdue-at-risk': [{}, {}, {}],
      '/api/risk-exceptions/overdue-review': [{}],
      '/api/risk-exceptions/active': []
    }
    globalThis.fetch = vi.fn(url => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(lists[url] ?? KPIS) }))
    const wrapper = await mountHome(['ViewDashboard', 'ApproveExceptions'])

    const card = name => wrapper.findAll('.home-area').find(c => c.get('h3').text() === name)
    expect(card('Accounts').get('.home-area-attention a').text()).toBe('3 accounts overdue or at risk')
    expect(card('Accounts').get('.home-area-attention a').attributes('href')).toBe('/reports-overdue-worklist')
    expect(card('Exceptions').findAll('.home-area-attention').map(p => p.text())).toEqual(['1 exception past its review date'])
  })

  it('does not ask for exceptions awaiting approval without ApproveExceptions (D-330)', async () => {
    await mountHome(['ViewDashboard'])

    expect(globalThis.fetch).not.toHaveBeenCalledWith('/api/risk-exceptions/active')
  })

  it('shows the KPI funnel to a user with ViewDashboard, each count opening Accounts filtered to it', async () => {
    const wrapper = await mountHome(['ViewDashboard'])

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/kpi-summary')
    const rows = wrapper.findAll('.home-funnel tbody tr')
    // D-326: the funnel starts with everything discovered, which has no Accounts list to open.
    expect(rows.map(r => r.findAll('td')[0].text())).toEqual(['Everything discovered', 'All accounts', 'In scope', 'Onboarded', 'Managed', 'Compliant'])
    expect(rows[0].find('a').exists()).toBe(false)
    expect(rows[1].findAll('td')[2].text()).toBe('80%')   // 200 tracked of 250 discovered
    expect(rows[2].find('a').attributes('href')).toBe('/account-progress-list?kpi=inScope')
    expect(rows[4].findAll('td')[2].text()).toBe('50%')   // managed 60 of onboarded 120
    expect(wrapper.text()).toContain('Out of scope (Risk Accepted / Excluded, or Archived): 50.')
    expect(cards(wrapper)).toEqual(['Dashboard', 'Accounts', 'Exceptions', 'Reports'])
  })

  it('offers an Auditor only the Admin area, and asks for no KPIs', async () => {
    const wrapper = await mountHome(['ViewAuditLog'], ['Auditor'])

    expect(globalThis.fetch).not.toHaveBeenCalled()
    expect(wrapper.find('.home-funnel').exists()).toBe(false)
    expect(cards(wrapper)).toEqual(['Admin'])
  })

  it('offers Reports to a user who may open only a report of its own permission', async () => {
    const wrapper = await mountHome(['ViewRiskReport', 'ManageTargets'])

    expect(cards(wrapper)).toEqual(['Targets', 'Reports', 'Admin'])
  })
})
