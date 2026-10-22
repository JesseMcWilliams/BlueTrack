import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import CpmHealth from './CpmHealth.vue'
import { formatDate } from '../../utils/formatDate'

// D-288: Reports > CPM Health.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

const router = () => createRouter({
  history: createMemoryHistory(),
  routes: [{ path: '/', component: { template: '<div />' } }, { path: '/accounts/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }]
})

describe('CpmHealth.vue', () => {
  afterEach(() => vi.restoreAllMocks())

  it('lists each failing account with its last CPM action and links to it', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([{
      accountKey: 12, accountName: 'root@host1', safeName: 'Unix', platformId: 'UnixSSH', sourceSystemName: 'Self-Hosted',
      cpmFailureSince: '2026-10-20T02:05:00', lastCPMAction: 'Change', lastCPMActionResult: 'Failure', lastVerifiedDate: '2026-10-01T00:00:00',
      lastCPMModifiedDate: null, autoManaged: false, manualReason: '(CPM)MaxRetries', ownerName: 'Ops'
    }])))
    const wrapper = mount(CpmHealth, { global: { plugins: [router()] } })
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/cpm-health')
    const cells = wrapper.findAll('tbody td').map(td => td.text())
    expect(cells).toEqual(['root@host1', 'Unix', 'UnixSSH', 'Self-Hosted', formatDate('2026-10-20'), 'Change (Failure)', formatDate('2026-10-01'), '', 'No ((CPM)MaxRetries)', 'Ops'])
    expect(wrapper.find('tbody a').attributes('href')).toBe('/accounts/12')
  })

  it('says when nothing fails', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([])))
    const wrapper = mount(CpmHealth, { global: { plugins: [router()] } })
    await flushPromises()
    expect(wrapper.text()).toContain("No account's CPM status is failure.")
  })
})
