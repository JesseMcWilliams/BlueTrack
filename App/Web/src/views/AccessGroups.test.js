import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import AccessGroups from './AccessGroups.vue'

// D-121: this page had zero filter/sort/count UI before this work, and
// gains two brand-new fields (SOR Type/SOR Address) plus a previously
// unrendered DiscoverySource column -- these tests cover the new filter
// dropdowns, the SOR fields round-tripping into the edit form, and the
// shared "Showing N of M total" count summary, following
// RiskScoreBands.test.js's own globalThis.fetch mocking style.
//
// D-124 Phase 3: also carries X-Filtered-Count (defaults to totalCount when
// not given separately, matching the common case of an unfiltered load).
function jsonResponse(body, { ok = true, status = 200, totalCount = null, filteredCount = totalCount } = {}) {
  return {
    ok,
    status,
    headers: {
      get: (name) => {
        if (name === 'X-Total-Count') return totalCount !== null ? String(totalCount) : null
        if (name === 'X-Filtered-Count') return filteredCount !== null ? String(filteredCount) : null
        return null
      }
    },
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
function mockInitialLoad({ groups = [sampleGroup], totalCount = groups.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/admin/access-groups/sor-types') return Promise.resolve(jsonResponse(sorTypes))
    if (url === '/api/admin/targets') return Promise.resolve(jsonResponse([]))
    if (url.startsWith('/api/admin/access-groups')) return Promise.resolve(jsonResponse(groups, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('AccessGroups.vue', () => {
  beforeEach(() => {
    // D-124 Phase 3: AccessGroups.vue now reads the shared, server-persisted
    // pageSize store (src/stores/pageSize.js) for pagination -- needs an
    // active Pinia instance, unlike before this phase.
    setActivePinia(createPinia())
  })

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

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ groups: [sampleGroup], totalCount: 30, filteredCount: 17 })

    const wrapper = mount(AccessGroups)
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 17 matching (30 total)')
  })

  // D-124 Phase 3: pagination.
  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ groups: [] })
    const wrapper = mount(AccessGroups)
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/access-groups?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('clicking Next advances to page 2 and re-fetches with page=2, resetting on a subsequent filter change', async () => {
    mockInitialLoad({ groups: [sampleGroup], totalCount: 120, filteredCount: 120 })
    const wrapper = mount(AccessGroups)
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Page 2 of')

    const scopeSelect = wrapper.findAll('select')[0]
    await scopeSelect.setValue('Local')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/access-groups?'))
    expect(calls.at(-1)).toContain('page=1')
    expect(calls.at(-1)).toContain('groupScope=Local')
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
