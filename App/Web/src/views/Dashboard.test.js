import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia } from 'pinia'
import Dashboard from './Dashboard.vue'
import { useRightsStore } from '../stores/rights'

// UI review W2-1: the stage counts open the accounts behind them.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const summary = [
  { stageOrder: 1, stageName: 'Discovery', statusName: 'Not Started', accountCount: 30 },
  { stageOrder: 1, stageName: 'Discovery', statusName: 'Complete', accountCount: 12 },
  { stageOrder: 2, stageName: 'Planning', statusName: 'Not Started', accountCount: 0 },
  { stageOrder: 2, stageName: 'Planning', statusName: 'Complete', accountCount: 0 },
  { stageOrder: 3, stageName: 'Onboarded to Vault', statusName: 'Complete', accountCount: 1234 }
]

function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: page },
      { path: '/accounts', name: 'account-progress-list', component: page },
      { path: '/reports/stage-status-summary', name: 'reports-stage-status-summary', component: page },
      { path: '/reports/kpi-summary', name: 'reports-kpi-summary', component: page },
      { path: '/reports/overdue', name: 'reports-overdue-worklist', component: page },
      { path: '/exceptions/overdue', name: 'risk-exceptions-overdue-worklist', component: page },
      { path: '/exceptions/approvals', name: 'risk-exceptions-approval-worklist', component: page }
    ]
  })
}

async function mountDashboard() {
  const router = makeRouter()
  await router.push('/')
  const pinia = createPinia()
  const rights = useRightsStore(pinia)
  rights.loaded = true
  rights.permissionNames = []
  const wrapper = mount(Dashboard, { global: { plugins: [router, pinia] } })
  await flushPromises()
  return { wrapper, router }
}

describe('Dashboard.vue', () => {
  beforeEach(() => {
    globalThis.fetch = vi.fn((url) => {
      if (url === '/api/reports/stage-status-summary') return Promise.resolve(jsonResponse(summary))
      if (url === '/api/reports/kpi-summary') return Promise.resolve(jsonResponse(null, false, 500))
      return Promise.resolve(jsonResponse([]))
    })
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('links each stage count to the Accounts list filtered to that stage', async () => {
    const { wrapper } = await mountDashboard()

    const link = wrapper.findAll('a').find(a => a.attributes('aria-label') === 'Show the 42 accounts in Discovery')
    expect(link.text()).toBe('42')
    expect(link.attributes('href')).toBe('/accounts?stage=Discovery')
    const big = wrapper.findAll('a').find(a => a.attributes('aria-label')?.includes('Onboarded to Vault'))
    expect(big.attributes('href')).toBe('/accounts?stage=Onboarded+to+Vault')
  })

  it('shows a stage with no accounts as plain 0, with nothing to open', async () => {
    const { wrapper } = await mountDashboard()

    const planning = wrapper.findAll('tbody tr').find(r => r.text().startsWith('Planning'))
    expect(planning.text()).toContain('0')
    expect(planning.find('a').exists()).toBe(false)
  })

  it('following the link lands on the list with the stage in the address', async () => {
    const { wrapper, router } = await mountDashboard()

    await wrapper.findAll('a').find(a => a.attributes('aria-label') === 'Show the 42 accounts in Discovery').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('account-progress-list')
    expect(router.currentRoute.value.query).toEqual({ stage: 'Discovery' })
  })
})
