import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import RiskExceptionsApprovalWorklist from './RiskExceptionsApprovalWorklist.vue'
import TargetsBulkImport from './TargetsBulkImport.vue'

// UI review W3-6 (the worklist links the account), W3-7 (the worklist says why there is no pending state) and W4-5
// (an import's pending matches link to their queue).
const stub = { template: '<div />' }
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/a/:accountKey', name: 'account-progress-detail', component: stub },
      { path: '/e/:exceptionKey', name: 'risk-exception-edit', component: stub },
      { path: '/admin/match-review', name: 'admin-target-match-review', component: stub },
      { path: '/admin/profiles/new', name: 'admin-import-mapping-profile-create', component: stub },
      { path: '/:p(.*)*', component: stub }
    ]
  })
}

describe('last review items', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('the approval worklist links an account-scoped exception to its account, and explains its purpose', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve([
      { exceptionKey: 1, exceptionID: 'EX-1', scopeType: 'Account', scopeName: 'svc_pay', accountKey: 41, justification: 'x', approvedByName: 'Pat', reviewDate: '2026-12-01' },
      { exceptionKey: 2, exceptionID: 'EX-2', scopeType: 'Application', scopeName: 'Payroll', applicationKey: 3, justification: 'y', approvedByName: 'Pat', reviewDate: '2026-12-01' }
    ]) }))
    const wrapper = mount(RiskExceptionsApprovalWorklist, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('p').text()).toContain('There is no separate pending state')
    const links = wrapper.findAll('tbody a').map(a => `${a.text()}=${a.attributes('href')}`)
    expect(links).toEqual(['EX-1=/e/1', 'svc_pay=/a/41', 'EX-2=/e/2'])
  })

  it('an import with pending matches links to Target Match Review', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST'
      ? { ok: true, status: 200, json: () => Promise.resolve({ totalRows: 3, createdCount: 1, mergedCount: 0, pendingReviewCount: 2, ignoredCount: 0, errors: [] }) }
      : { ok: true, status: 200, json: () => Promise.resolve([]) }))
    const wrapper = mount(TargetsBulkImport, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    const input = wrapper.find('input[type="file"]')
    Object.defineProperty(input.element, 'files', { value: [new File(['x'], 'targets.csv')], configurable: true })
    await input.trigger('change')
    await wrapper.findAll('button').find(b => b.text() === 'Import').trigger('click')
    await flushPromises()

    expect(wrapper.find('a[href="/admin/match-review"]').text()).toBe('Review the pending matches in Target Match Review')
  })
})
