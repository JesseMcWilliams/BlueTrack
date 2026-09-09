import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AccessGroups from './AccessGroups.vue'

// D-121: this page had zero filter/sort/count UI before this work, and
// gains two brand-new fields (SOR Type/SOR Address) plus a previously
// unrendered DiscoverySource column -- these tests cover the new filter
// dropdowns, the SOR fields round-tripping into the edit form, and the
// shared "Showing N of M total" count summary, following
// RiskScoreBands.test.js's own globalThis.fetch mocking style.
function jsonResponse(body, { ok = true, status = 200, totalCount = null } = {}) {
  return {
    ok,
    status,
    headers: { get: (name) => (name === 'X-Total-Count' && totalCount !== null ? String(totalCount) : null) },
    json: () => Promise.resolve(body)
  }
}

const sampleGroup = {
  accessGroupKey: 1,
  groupName: 'Server Admins',
  groupIdentifier: 'CN=ServerAdmins',
  groupScope: 'Domain',
  foundOnTargetKey: null,
  foundOnTargetName: null,
  discoverySource: 'AD Discovery',
  sorTypeKey: 1,
  sorTypeName: 'Domain',
  sorAddress: 'company.com',
  baseRiskScore: 300,
  computedRiskScore: null,
  isRiskScoreStale: true,
  description: null,
  modifiedDate: null
}

const sorTypes = [
  { sorTypeKey: 1, sorTypeName: 'Domain' },
  { sorTypeKey: 2, sorTypeName: 'Local' },
  { sorTypeKey: 3, sorTypeName: 'App' }
]

// Mounting triggers: 1) GET /api/admin/access-groups/sor-types, then
// load()'s Promise.all([GET /api/admin/access-groups, GET /api/admin/targets]).
function mockInitialLoad({ groups = [sampleGroup], totalCount = groups.length } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/admin/access-groups/sor-types') return Promise.resolve(jsonResponse(sorTypes))
    if (url === '/api/admin/targets') return Promise.resolve(jsonResponse([]))
    if (url.startsWith('/api/admin/access-groups')) return Promise.resolve(jsonResponse(groups, { totalCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('AccessGroups.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the group inventory, including SOR Type/SOR Address/Discovery Source', async () => {
    mockInitialLoad()

    const wrapper = mount(AccessGroups)
    await flushPromises()

    expect(wrapper.text()).toContain('Server Admins')
    expect(wrapper.text()).toContain('Domain') // SOR Type column
    expect(wrapper.text()).toContain('company.com') // SOR Address column
    expect(wrapper.text()).toContain('AD Discovery') // Discovery Source column
  })

  it('shows the "Showing N of M total" count summary once X-Total-Count is known', async () => {
    mockInitialLoad({ groups: [sampleGroup], totalCount: 17 })

    const wrapper = mount(AccessGroups)
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1 of 17 total')
  })

  it('sends the Scope filter as a groupScope query param', async () => {
    mockInitialLoad({ groups: [] })
    const wrapper = mount(AccessGroups)
    await flushPromises()

    const scopeSelect = wrapper.findAll('select')[0]
    await scopeSelect.setValue('Local')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/access-groups?'))
    expect(calls.some(u => u.includes('groupScope=Local'))).toBe(true)
  })

  it('opens the New Access Group form with SOR Type/SOR Address fields available', async () => {
    mockInitialLoad({ groups: [] })
    const wrapper = mount(AccessGroups)
    await flushPromises()

    await wrapper.get('button.btn-primary').trigger('click')

    expect(wrapper.find('form').exists()).toBe(true)
    expect(wrapper.text()).toContain('SOR Type')
    expect(wrapper.text()).toContain('SOR Address')
  })

  it('edit form pre-fills the existing SOR Type/SOR Address values', async () => {
    mockInitialLoad()
    const wrapper = mount(AccessGroups)
    await flushPromises()

    const editButton = wrapper.findAll('button').find(b => b.text() === 'Edit')
    await editButton.trigger('click')

    const sorAddressInput = wrapper.findAll('input').find(i => i.element.value === 'company.com')
    expect(sorAddressInput).toBeTruthy()
  })
})
