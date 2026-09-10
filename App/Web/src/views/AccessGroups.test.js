import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccessGroups from './AccessGroups.vue'

// D-128/D-129: Delete now awaits the shared confirmDelete(...) dialog
// (mounted separately in App.vue, not present in this isolated mount) --
// mocked here so this file's own tests stay focused on AccessGroups.vue's
// delete-wiring/message content, not the shared dialog's own UI.
vi.mock('../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../composables/useConfirmDialog'

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

// Mounting triggers: GET /api/admin/access-groups/sor-types, then load()'s
// GET /api/admin/access-groups. (D-124 Phase 4: load() no longer also
// fetches /api/admin/targets -- that only ever fed the inline form's
// "Found On Target" dropdown, which moved to AccessGroupEdit.vue.)
function mockInitialLoad({ groups = [sampleGroup], totalCount = groups.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/admin/access-groups/sor-types') return Promise.resolve(jsonResponse(sorTypes))
    if (url.startsWith('/api/admin/access-groups')) return Promise.resolve(jsonResponse(groups, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

// D-124 Phase 4: AccessGroups.vue now links "+ New Access Group"/each row's
// "Edit" to AccessGroupEdit.vue's routes instead of toggling an inline
// form -- needs a real router installed so <router-link> resolves, same
// pattern RiskExceptionsList.test.js already established.
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/access-groups', name: 'access-groups', component: { template: '<div />' } },
      { path: '/access-groups/new', name: 'access-group-create', component: { template: '<div />' } },
      { path: '/access-groups/:accessGroupKey', name: 'access-group-edit', component: { template: '<div />' } },
      { path: '/access-groups/bulk-import', name: 'access-groups-bulk-import', component: { template: '<div />' } }
    ]
  })
}

describe('AccessGroups.vue', () => {
  beforeEach(() => {
    // D-124 Phase 3: AccessGroups.vue now reads the shared, server-persisted
    // pageSize store (src/stores/pageSize.js) for pagination -- needs an
    // active Pinia instance, unlike before this phase.
    setActivePinia(createPinia())
    // vi.restoreAllMocks() below clears confirmDelete's mocked resolved
    // value after the first test that uses it -- re-establish it fresh
    // before every test (see the identical note in RiskScoreBands.test.js).
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the group inventory, including SOR Type/SOR Address/Discovery Source', async () => {
    mockInitialLoad()

    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Server Admins')
    expect(wrapper.text()).toContain('Domain') // SOR Type column
    expect(wrapper.text()).toContain('company.com') // SOR Address column
    expect(wrapper.text()).toContain('AD Discovery') // Discovery Source column
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ groups: [sampleGroup], totalCount: 30, filteredCount: 17 })

    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 17 matching (30 total)')
  })

  // D-124 Phase 3: pagination.
  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ groups: [] })
    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/access-groups?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('clicking Next advances to page 2 and re-fetches with page=2, resetting on a subsequent filter change', async () => {
    mockInitialLoad({ groups: [sampleGroup], totalCount: 120, filteredCount: 120 })
    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
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
    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const scopeSelect = wrapper.findAll('select')[0]
    await scopeSelect.setValue('Local')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/access-groups?'))
    expect(calls.some(u => u.includes('groupScope=Local'))).toBe(true)
  })

  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // AccessGroupEdit.vue's routed pages -- SOR Type/SOR Address field
  // coverage (including edit-mode pre-fill) moved to AccessGroupEdit.test.js.
  // D-125: "+ New Access Group" is now a real <button> (this app's button
  // styling is scoped to the button element itself, not a reusable class),
  // navigating imperatively via router.push -- asserts on the router's
  // resulting location instead of an href.
  it('"+ New Access Group" button navigates to the access-group-create route', async () => {
    mockInitialLoad({ groups: [] })
    const router = makeRouter()
    const wrapper = mount(AccessGroups, { global: { plugins: [router] } })
    await flushPromises()

    const button = wrapper.findAll('button').find(b => b.text() === '+ New Access Group')
    await button.trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('access-group-create')
  })

  // D-125: clicking the Name is the edit action -- there's no separate
  // "Edit" link/button in the row anymore.
  it('links each row\'s Name to the access-group-edit route with that row\'s key', async () => {
    mockInitialLoad()
    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const link = wrapper.findAll('a').find(a => a.text() === sampleGroup.groupName)
    expect(link.attributes('href')).toBe(`/access-groups/${sampleGroup.accessGroupKey}`)
  })

  // D-124 Phase 5: the 3 inline "Bulk Import" sections moved off this page
  // onto AccessGroupsBulkImport.test.js -- coverage here is limited to the
  // "Bulk Actions" link itself, matching how the "+ New Access Group"/"Edit"
  // router-links are asserted above.
  it('"Bulk Actions" button navigates to the access-groups-bulk-import route', async () => {
    mockInitialLoad({ groups: [] })
    const router = makeRouter()
    const wrapper = mount(AccessGroups, { global: { plugins: [router] } })
    await flushPromises()

    const button = wrapper.findAll('button').find(b => b.text() === 'Bulk Actions')
    await button.trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('access-groups-bulk-import')
  })

  // D-129: the confirmation names the specific record and shows a
  // structured Name/Scope/Address/Source summary, not just the row's name.
  it('confirms delete with a Name/Scope/Address/Source summary before sending the DELETE request', async () => {
    mockInitialLoad()
    const wrapper = mount(AccessGroups, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const deleteButton = wrapper.findAll('button').find(b => b.text() === 'Delete')
    await deleteButton.trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith([
      'Delete Access Group',
      `Name: ${sampleGroup.groupName}`,
      `Scope: ${sampleGroup.groupScope}`,
      `Address: ${sampleGroup.sorAddress}`,
      `Source: ${sampleGroup.discoverySource}`,
      'This cannot be undone.'
    ].join('\n'))
    expect(globalThis.fetch).toHaveBeenCalledWith(`/api/admin/access-groups/${sampleGroup.accessGroupKey}`, { method: 'DELETE' })
  })
})
