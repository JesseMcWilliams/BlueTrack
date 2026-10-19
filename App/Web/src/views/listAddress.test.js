import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import Targets from './Targets.vue'
import AccessGroups from './AccessGroups.vue'
import RiskExceptionsList from './RiskExceptionsList.vue'
import AuditLogViewer from './admin/AuditLogViewer.vue'
import { useListStateStore } from '../stores/listState'

// UI review W4 (D-252's pattern): the Targets and Access Groups lists keep filters, sort and page in the address.
function answer(body = []) {
  return { ok: true, status: 200, headers: { get: (n) => (n === 'X-Total-Count' || n === 'X-Filtered-Count' ? '300' : null) }, json: () => Promise.resolve(body) }
}

async function mountAt(component, path, name) {
  globalThis.fetch = vi.fn(() => Promise.resolve(answer()))
  const stub = { template: '<div />' }
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: stub }, { path, name, component },
      { path: '/x/e/:exceptionKey', name: 'risk-exception-edit', component: stub },
      { path: '/x/n', name: 'risk-exception-create', component: stub },
      { path: '/x/b', name: 'risk-exceptions-bulk-import', component: stub },
      { path: '/x/o', name: 'risk-exceptions-overdue-worklist', component: stub },
      { path: '/x/a', name: 'risk-exceptions-approval-worklist', component: stub },
      { path: '/:pathMatch(.*)*', component: stub }
    ]
  })
  return { router, mount: async (query) => {
    router.push({ name, query })
    await router.isReady()
    const wrapper = mount(component, { global: { plugins: [router] } })
    await flushPromises()
    return wrapper
  } }
}

describe.each([
  ['Targets', Targets, '/targets', 'targets', '/api/admin/targets?', { type: '3', application: '7', linked: 'Show', sort: 'riskScore:desc', page: '2' }, ['targetTypeKey=3', 'applicationKey=7', 'linked=Show', 'sort=riskScore%3Adesc', 'page=2']],
  ['Risk Exceptions', RiskExceptionsList, '/exceptions', 'risk-exceptions-list', '/api/risk-exceptions?', { status: 'Revoked', scopeType: 'Account', sort: 'reviewDate:desc', page: '2' }, ['status=Revoked', 'scopeType=Account', 'sort=reviewDate%3Adesc', 'page=2']],
  ['Audit Log', AuditLogViewer, '/admin/audit-log', 'admin-audit-log', '/api/audit-log?', { eventType: 'Login', entityName: 'Role', from: '2026-10-01', to: '2026-10-19', sort: 'occurredAt:asc', page: '2' }, ['eventType=Login', 'entityName=Role', 'fromDate=2026-10-01', 'toDate=2026-10-19', 'sort=occurredAt%3Aasc', 'page=2']],
  ['Access Groups', AccessGroups, '/access-groups', 'access-groups', '/api/admin/access-groups?', { scope: 'Local', sorType: 'CyberArk', sort: 'groupName:asc', page: '3' }, ['groupScope=Local', 'sorTypeName=CyberArk', 'sort=groupName%3Aasc', 'page=3']]
])('%s list address', (_label, component, path, name, apiPrefix, query, expected) => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('loads what the address says', async () => {
    const { mount } = await mountAt(component, path, name)
    await mount(query)
    const url = globalThis.fetch.mock.calls.map(c => c[0]).find(u => u.startsWith(apiPrefix))
    for (const part of expected) expect(url, part).toContain(part)
  })

  it('writes a changed page into the address and remembers the view for the way back', async () => {
    const { router, mount } = await mountAt(component, path, name)
    const wrapper = await mount({})
    const next = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await next.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.query.page).toBe('2')
    if (name !== 'admin-audit-log') expect(useListStateStore().queryFor(name)).toEqual({ page: '2' }) // the Audit Log has no way back to remember
  })

  it('ignores a value it does not have', async () => {
    const { mount } = await mountAt(component, path, name)
    await mount({ page: 'abc', sort: 'nonsense:desc', linked: 'Maybe', scope: 'Galaxy' })
    const url = globalThis.fetch.mock.calls.map(c => c[0]).find(u => u.startsWith(apiPrefix))
    expect(url).toContain('page=1')
    expect(url).not.toContain('nonsense')
    expect(url).not.toContain('Maybe')
    expect(url).not.toContain('Galaxy')
  })
})
