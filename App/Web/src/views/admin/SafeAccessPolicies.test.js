import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { useRightsStore } from '../../stores/rights'
import SafeAccessPolicies from './SafeAccessPolicies.vue'
import SafeAccessPolicyEdit from './SafeAccessPolicyEdit.vue'
import SafeRoleEdit from './SafeRoleEdit.vue'
import SafeAccessReport from '../reports/SafeAccessReport.vue'
import ComplianceSection from '../../components/ComplianceSection.vue'

// D-300/D-301: Admin > Safe Access Policies (with its role and policy forms), Reports > Safe Access and the account
// page's Compliance section.
vi.mock('../../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))

function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: page },
      { path: '/admin/safe-access-policies', name: 'admin-safe-access-policies', component: page },
      { path: '/admin/safe-access-policies/new', name: 'admin-safe-access-policy-create', component: page },
      { path: '/admin/safe-access-policies/:policyKey', name: 'admin-safe-access-policy-edit', component: page },
      { path: '/admin/safe-roles/new', name: 'admin-safe-role-create', component: page },
      { path: '/admin/safe-roles/:roleKey', name: 'admin-safe-role-edit', component: page },
      { path: '/reports/safe-access', name: 'reports-safe-access', component: page }
    ]
  })
}

const roles = [{ safeRoleKey: 4, roleName: 'End user', description: null, exactMatch: false, permissions: ['ListAccounts', 'UseAccounts', 'RetrieveAccounts'], ruleCount: 1 }]
const policies = [{
  safeAccessPolicyKey: 2, policyName: 'Apps', safeNamePattern: 'APP_*', sortOrder: 1, isEnabled: true, description: null,
  rules: [
    { ruleType: 'Required', memberPattern: '{SafeName}_Users', safeRoleKey: 4, safeRoleName: 'End user', requirePeople: true },
    { ruleType: 'Forbidden', memberPattern: 'Vault Admins', safeRoleKey: null, safeRoleName: null, requirePeople: false }
  ]
}]

describe('Safe access policies', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('lists the policies with their rules and the roles, and Check now runs the check', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') return Promise.resolve(jsonResponse(null, { status: 204 }))
      return Promise.resolve(jsonResponse(url.endsWith('/roles') ? roles : policies))
    })
    const wrapper = mount(SafeAccessPolicies, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.findAll('.rules li').map(li => li.text())).toEqual([
      'Required: {SafeName}_Users holds End user, with people in it', 'Forbidden: Vault Admins'
    ])
    expect(wrapper.find('table.roles').text()).toContain('ListAccounts, UseAccounts, RetrieveAccounts')
    expect(wrapper.find('table.roles').text()).toContain('1 rule')

    await wrapper.findAll('button').find(b => b.text() === 'Check now').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/admin/safe-access/check' && init?.method === 'POST')).toBe(true)
  })

  it('a new policy: a Required rule picks a role, a Forbidden one may name none, and Save sends both', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') return Promise.resolve(jsonResponse({ safeAccessPolicyKey: 3 }, { status: 201 }))
      return Promise.resolve(jsonResponse(url.endsWith('/roles') ? roles : policies))
    })
    const wrapper = mount(SafeAccessPolicyEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('input[type="number"]').element.value).toBe('2')   // after the last policy
    await wrapper.find('input[maxlength="200"]').setValue('Unix')
    await wrapper.findAll('input[maxlength="300"]')[0].setValue('UNIX_*')
    await wrapper.find('.rule input').setValue('{SafeName}_Admins')
    await wrapper.findAll('button').find(b => b.text() === 'Add a Forbidden member').trigger('click')
    await wrapper.findAll('.rule')[1].find('input').setValue('Vault Admins')
    expect(wrapper.findAll('.rule')[1].findAll('option').map(o => o.text())).toEqual(['Any membership', 'End user'])

    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const saved = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/safe-access/policies' && init?.method === 'POST')
    expect(JSON.parse(saved[1].body)).toMatchObject({
      policyName: 'Unix', safeNamePattern: 'UNIX_*', sortOrder: 2,
      rules: [
        { ruleType: 'Required', memberPattern: '{SafeName}_Admins', safeRoleKey: 4, requirePeople: false },
        { ruleType: 'Forbidden', memberPattern: 'Vault Admins', safeRoleKey: null, requirePeople: false }
      ]
    })
  })

  it('Duplicate opens a new policy or role filled from the one copied; a policy copy starts disabled', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') return Promise.resolve(jsonResponse({ safeAccessPolicyKey: 3, safeRoleKey: 5 }, { status: 201 }))
      if (url.endsWith('/permissions')) return Promise.resolve(jsonResponse(['UseAccounts', 'RetrieveAccounts', 'ListAccounts']))
      return Promise.resolve(jsonResponse(url.endsWith('/roles') ? roles : policies))
    })
    const router = makeRouter()
    const list = mount(SafeAccessPolicies, { global: { plugins: [router] } })
    await flushPromises()
    const duplicates = list.findAll('button').filter(b => b.text() === 'Duplicate')
    expect(duplicates).toHaveLength(2)   // one policy, one role
    await duplicates[0].trigger('click')
    await flushPromises()
    expect(router.currentRoute.value).toMatchObject({ name: 'admin-safe-access-policy-create', query: { copyOf: '2' } })

    const policy = mount(SafeAccessPolicyEdit, { global: { plugins: [router] } })
    await flushPromises()
    expect(policy.find('.copied-from').text()).toContain('A copy of Apps')
    await policy.find('form').trigger('submit')
    await flushPromises()
    const savedPolicy = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/safe-access/policies' && init?.method === 'POST')
    expect(JSON.parse(savedPolicy[1].body)).toEqual({
      policyName: 'Apps (copy)', safeNamePattern: 'APP_*', sortOrder: 2, isEnabled: false, description: '',
      rules: [
        { ruleType: 'Required', memberPattern: '{SafeName}_Users', safeRoleKey: 4, requirePeople: true },
        { ruleType: 'Forbidden', memberPattern: 'Vault Admins', safeRoleKey: null, requirePeople: false }
      ]
    })

    await router.push({ name: 'admin-safe-role-create', query: { copyOf: '4' } })
    const role = mount(SafeRoleEdit, { global: { plugins: [router] } })
    await flushPromises()
    await role.find('form').trigger('submit')
    await flushPromises()
    const savedRole = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/safe-access/roles' && init?.method === 'POST')
    expect(JSON.parse(savedRole[1].body)).toEqual({ roleName: 'End user (copy)', description: '', exactMatch: false, permissions: ['ListAccounts', 'UseAccounts', 'RetrieveAccounts'] })
  })

  it('a Safe role saves the ticked permissions', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') return Promise.resolve(jsonResponse({ safeRoleKey: 5 }, { status: 201 }))
      return Promise.resolve(jsonResponse(['UseAccounts', 'RetrieveAccounts', 'ListAccounts']))
    })
    const wrapper = mount(SafeRoleEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.find('input[maxlength="100"]').setValue('Retrieve only')
    const boxes = wrapper.findAll('.permissions input')
    await boxes[1].setValue(true)
    await boxes[2].setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const saved = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(JSON.parse(saved[1].body)).toMatchObject({ roleName: 'Retrieve only', permissions: ['RetrieveAccounts', 'ListAccounts'], exactMatch: false })
  })

  it('the report lists failing Safes with what failed, and a reviewer reviews a Safe\'s accounts', async () => {
    useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames: ['ViewDashboard', 'ReviewAccountCompliance'] })
    const rows = [
      { safeKey: 1, safeName: 'APP_Pay', source: 'SELFHOSTED', policyName: 'Apps', isCompliant: false, checkedAt: '2026-11-03T02:10:00Z', accountCount: 3, reviewedCount: 1, problems: ['Vault Admins: must not be a member.'] },
      { safeKey: 2, safeName: 'APP_Hr', source: 'SELFHOSTED', policyName: 'Apps', isCompliant: true, checkedAt: '2026-11-03T02:10:00Z', accountCount: 2, reviewedCount: 2, problems: [] }
    ]
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST' ? jsonResponse({ reviewedCount: 2 }) : jsonResponse(rows)))
    const wrapper = mount(SafeAccessReport, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const cells = wrapper.findAll('tbody tr')[0].findAll('td').map(td => td.text())
    expect(cells.slice(1, 5)).toEqual(['Apps', 'Failed', 'Vault Admins: must not be a member.', '1 of 3'])
    // Every account of APP_Hr is reviewed: no button there.
    expect(wrapper.findAll('tbody tr')[1].find('button').exists()).toBe(false)
    await wrapper.find('input[type="checkbox"]').setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)

    await wrapper.find('tbody button').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/reports/safe-access/1/review' && init?.method === 'POST')).toBe(true)
  })

  it('the account page shows the Safe check and lets a reviewer mark it reviewed; others only read it', async () => {
    useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames: ['ViewDashboard', 'ReviewAccountCompliance'] })
    let reviewed = false
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'PUT') { reviewed = true; return Promise.resolve(jsonResponse(null, { status: 204 })) }
      return Promise.resolve(jsonResponse({
        safeName: 'APP_Pay', policyName: 'Apps', isCompliant: false, checkedAt: '2026-11-03T02:10:00Z', problems: ['Vault Admins: must not be a member.'],
        reviewedByName: reviewed ? 'Pat' : null, reviewedAt: reviewed ? '2026-11-03T15:00:00Z' : null, reviewNote: null
      }))
    })
    const wrapper = mount(ComplianceSection, { props: { accountKey: 9 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('.compliance-failed').text()).toContain('failed the policy "Apps"')
    expect(wrapper.find('.compliance-problems').text()).toBe('Vault Admins: must not be a member.')
    expect(wrapper.text()).toContain('Compliance not reviewed.')
    await wrapper.find('input').setValue('Owner agreed')
    await wrapper.findAll('button').find(b => b.text() === 'Mark reviewed').trigger('click')
    await flushPromises()
    const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(put[0]).toBe('/api/account-progress/9/compliance-review')
    expect(JSON.parse(put[1].body)).toEqual({ note: 'Owner agreed' })
    expect(wrapper.find('.compliance-reviewed').text()).toContain('Compliance reviewed by Pat')
    expect(wrapper.findAll('button').map(b => b.text())).toContain('Clear review')

    useRightsStore().$patch({ permissionNames: ['ViewDashboard'] })
    await flushPromises()
    expect(wrapper.find('.compliance-actions').exists()).toBe(false)
  })
})
