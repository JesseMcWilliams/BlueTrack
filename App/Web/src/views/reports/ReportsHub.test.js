import { describe, it, expect, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { h } from 'vue'
import { createRouter, createMemoryHistory, RouterView } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import ReportsHub from './ReportsHub.vue'
import { useRightsStore } from '../../stores/rights'

// D-220: the reports are listed in a column on the left (as on the Admin
// hub), each permission-gated report shown only to holders of its permission.
const names = [
  'reports-overdue-worklist', 'reports-stage-status-summary', 'reports-reconciliation-review',
  'reports-possible-matches', 'reports-unresolved-entitlement-members', 'reports-risk-score',
  'reports-risk-exception-sod', 'reports-discovered-accounts', 'reports-decom-safes',
  'reports-decom-accounts', 'reports-kpi-summary'
]

async function mountHub(permissionNames) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{
      path: '/reports',
      component: ReportsHub,
      children: names.map(name => ({ path: name, name, component: { template: `<p class="report">${name}</p>` } }))
    }]
  })
  await router.push('/reports/reports-kpi-summary')
  useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames })
  // Rendered through the app's own router-view, so the hub's router-view shows the open report.
  return mount({ render: () => h(RouterView) }, { global: { plugins: [router] } })
}

const links = wrapper => wrapper.findAll('nav a').map(a => a.text())

describe('ReportsHub.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('lists every report, in order, in a labelled column beside the report', async () => {
    const wrapper = await mountHub(['ViewDashboard', 'ConfirmReconciliation', 'ViewRiskReport', 'ViewRiskExceptionSodReport', 'ViewDiscoveredAccounts'])
    const nav = wrapper.get('nav.reports-subnav')

    expect(nav.attributes('aria-label')).toBe('Reports')
    expect(links(wrapper)).toEqual([
      'Overdue / At-Risk', 'Stage/Status Summary', 'Reconciliation Review', 'Possible Account Matches',
      'Unresolved Entitlement Members', 'Risk Score', 'Risk Exception SoD', 'Discovered Accounts',
      'Safes Flagged for Deletion', 'Accounts Flagged for Deletion', 'KPI Summary'
    ])
    // The column and the report sit side by side, as on the Admin hub.
    expect(wrapper.get('.reports-layout').element.children[0]).toBe(nav.element)
    expect(wrapper.get('.reports-content').text()).toContain('reports-kpi-summary')
  })

  it('hides the reports whose permission the user lacks', async () => {
    const wrapper = await mountHub(['ViewDashboard'])

    expect(links(wrapper)).toEqual([
      'Overdue / At-Risk', 'Stage/Status Summary', 'Unresolved Entitlement Members',
      'Safes Flagged for Deletion', 'Accounts Flagged for Deletion', 'KPI Summary'
    ])
  })

  it('marks the open report as the current page', async () => {
    const wrapper = await mountHub(['ViewDashboard'])
    const current = wrapper.findAll('nav a').filter(a => a.attributes('aria-current') === 'page')

    expect(current.map(a => a.text())).toEqual(['KPI Summary'])
  })
})
