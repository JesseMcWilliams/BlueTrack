import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import UnresolvedEntitlementMembers from './UnresolvedEntitlementMembers.vue'
import { useRightsStore } from '../../stores/rights'

// D-232: the member's name, the Show resolved filter, and Resolve / Undo
// (ConfirmReconciliation) on Reports > Unresolved Entitlement Members.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

const unresolved = {
  entitlementKey: 1, safeKey: 3, safeName: 'Finance-Safe', memberType: 'User',
  unresolvedMemberId: '6a1f-guid', memberName: 'jane.doe@corp.example', resolutionMethod: null,
  userKey: null, groupKey: null, resolvedToName: null, resolutionNote: null, resolvedByName: null,
  membershipExpirationDate: null, snapshotDate: '2026-10-14T00:00:00'
}
const external = {
  ...unresolved, entitlementKey: 2, memberType: 'Group', unresolvedMemberId: 'DPA_Admin_Role', memberName: 'DPA_Admin_Role',
  resolutionMethod: 'External', resolvedByName: 'Pat Approver', resolutionNote: 'Built-in cloud role'
}
const byName = {
  ...unresolved, entitlementKey: 3, unresolvedMemberId: '7b2e-guid', memberName: 'sam.lee', resolutionMethod: 'ByName',
  userKey: 41, resolvedToName: 'sam.lee'
}
const janeUser = { key: 40, name: 'jane.doe@corp.example', detail: 'jane.doe@corp.example' }

function mountPage(permissionNames) {
  useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames })
  return mount(UnresolvedEntitlementMembers, { attachTo: document.body })
}

describe('UnresolvedEntitlementMembers.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  it('shows the member name and ID, unresolved only, with no actions without ConfirmReconciliation', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([unresolved])))
    const wrapper = mountPage(['ViewDashboard'])
    await flushPromises()

    expect(globalThis.fetch.mock.calls[0][0]).toBe('/api/reports/unresolved-entitlement-members?showResolved=false')
    const headers = wrapper.findAll('th').map(th => th.text())
    expect(headers).toEqual(['Safe', 'Member Type', 'Name', 'Member ID', 'Resolution', 'Membership Expiration', 'Snapshot Date'])
    const cells = wrapper.findAll('tbody td').map(td => td.text())
    expect(cells.slice(0, 5)).toEqual(['Finance-Safe', 'User', 'jane.doe@corp.example', '6a1f-guid', 'Unresolved'])
    expect(wrapper.findAll('button').filter(b => b.text() === 'Resolve')).toHaveLength(0)
  })

  it('Show resolved reloads with resolved rows and says how each was resolved', async () => {
    globalThis.fetch = vi.fn(url => Promise.resolve(jsonResponse(url.includes('showResolved=true') ? [unresolved, external, byName] : [unresolved])))
    const wrapper = mountPage(['ViewDashboard', 'ConfirmReconciliation'])
    await flushPromises()

    await wrapper.find('input[type="checkbox"]').setValue(true)
    await flushPromises()

    expect(globalThis.fetch.mock.calls.at(-1)[0]).toBe('/api/reports/unresolved-entitlement-members?showResolved=true')
    expect(wrapper.text()).toContain('External / cloud role (by Pat Approver): Built-in cloud role')
    expect(wrapper.text()).toContain('Matched by name: sam.lee')
    // Undo only for a manual resolution (External here), not a name match.
    expect(wrapper.findAll('button').filter(b => b.text() === 'Undo')).toHaveLength(1)
  })

  it('Resolve pre-fills the one user with exactly that name, and posts the link', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') return Promise.resolve(jsonResponse(null, { status: 204 }))
      if (url.includes('/principals')) return Promise.resolve(jsonResponse([janeUser, { key: 99, name: 'jane.doe@corp.example.old' }]))
      return Promise.resolve(jsonResponse([unresolved]))
    })
    const wrapper = mountPage(['ViewDashboard', 'ConfirmReconciliation'])
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Resolve').trigger('click')
    await flushPromises()

    expect(wrapper.find('h3').text()).toBe('Resolve jane.doe@corp.example')
    expect(globalThis.fetch.mock.calls.some(([url]) => url === '/api/reports/unresolved-entitlement-members/principals?memberType=User&q=jane.doe%40corp.example')).toBe(true)
    expect(wrapper.find('input[role="combobox"]').element.value).toBe('jane.doe@corp.example')

    await wrapper.findAll('button').find(b => b.text() === 'Save').trigger('click')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post[0]).toBe('/api/reports/unresolved-entitlement-members/resolve')
    expect(JSON.parse(post[1].body)).toEqual({
      memberType: 'User', sourceMemberId: '6a1f-guid', resolution: 'Linked', userKey: 40, groupKey: null, note: null
    })
    expect(wrapper.find('h3').exists()).toBe(false)
  })

  it('External / cloud role posts the note; a refusal shows the API message', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') return Promise.resolve(jsonResponse({ detail: 'No current entitlement has this member ID.' }, { ok: false, status: 404 }))
      if (url.includes('/principals')) return Promise.resolve(jsonResponse([]))
      return Promise.resolve(jsonResponse([unresolved]))
    })
    const wrapper = mountPage(['ViewDashboard', 'ConfirmReconciliation'])
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Resolve').trigger('click')
    await flushPromises()
    // Linked with nothing chosen is refused in the browser.
    await wrapper.findAll('button').find(b => b.text() === 'Save').trigger('click')
    expect(wrapper.find('[role="alert"]').text()).toBe('Choose the user to link this member to.')

    await wrapper.find('input[value="External"]').setValue(true)
    await wrapper.find('textarea').setValue('Entra guest')
    await wrapper.findAll('button').find(b => b.text() === 'Save').trigger('click')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(JSON.parse(post[1].body)).toMatchObject({ resolution: 'External', note: 'Entra guest', userKey: null })
    expect(wrapper.find('[role="alert"]').text()).toContain('No current entitlement has this member ID.')
  })

  it('Undo asks first, posts the member, and reloads', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST' ? jsonResponse(null, { status: 204 }) : jsonResponse([external])))
    const wrapper = mountPage(['ViewDashboard', 'ConfirmReconciliation'])
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Undo').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('DPA_Admin_Role'), 'Undo')
    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post[0]).toBe('/api/reports/unresolved-entitlement-members/unresolve')
    expect(JSON.parse(post[1].body)).toEqual({ memberType: 'Group', sourceMemberId: 'DPA_Admin_Role' })
    expect(globalThis.fetch.mock.calls.filter(([url]) => url.includes('showResolved'))).toHaveLength(2)
  })
})
