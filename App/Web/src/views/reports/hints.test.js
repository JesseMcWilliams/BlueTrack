import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import ReconciliationReviewQueue from './ReconciliationReviewQueue.vue'
import DecomSafes from './DecomSafes.vue'
import DecomAccounts from './DecomAccounts.vue'

// UI review W5-3 and W6-3: the Reconciliation Review page says what it is (and that it is read-only), and the
// decommission reports say what to do next.
function mountPage(component) {
  globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([]) }))
  const stub = { template: '<div />' }
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/pm', name: 'reports-possible-matches', component: stub },
      { path: '/da', name: 'reports-decom-accounts', component: stub },
      { path: '/ds', name: 'reports-decom-safes', component: stub },
      { path: '/a/:accountKey', name: 'account-progress-detail', component: stub }
    ]
  })
  return mount(component, { global: { plugins: [router] } })
}

describe('report hints', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('Reconciliation Review says it is read-only and links to Possible Account Matches', async () => {
    const wrapper = mountPage(ReconciliationReviewQueue)
    await flushPromises()
    expect(wrapper.find('.hint').text()).toContain('This page is read-only')
    expect(wrapper.find('.hint a').attributes('href')).toBe('/pm')
  })

  it.each([
    ['Safes Flagged for Deletion', DecomSafes],
    ['Accounts Flagged for Deletion', DecomAccounts]
  ])('%s says BlueTrack only reports these and what to do', async (_name, component) => {
    const wrapper = mountPage(component)
    await flushPromises()
    expect(wrapper.text()).toContain('BlueTrack only reports these.')
    expect(wrapper.text()).toContain('in CyberArk; this report updates after the next data load.')
  })
})
