import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import Targets from './Targets.vue'

// D-121: this page had zero filter/sort/count UI before this work -- these
// tests establish coverage for the new filter dropdowns and the shared
// "Showing N of M total" count summary (FilterCountSummary + useTotalCount),
// following RiskScoreBands.test.js's own globalThis.fetch mocking style
// (the freshest Vitest component-test precedent in this project at the
// time this page was moved out of admin/ and given filter UI for the
// first time).
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

const sampleTarget = {
  targetKey: 1,
  targetTypeKey: 1,
  targetTypeCode: 'Server',
  targetTypeDisplayName: 'Server',
  targetName: 'web01',
  applicationKey: null,
  applicationName: null,
  riskScore: 500,
  description: null,
  discoverySource: 'Manual',
  modifiedDate: null,
  identifiers: [{ identifierType: 'Hostname', identifierValue: 'web01.example.com' }]
}

// D-124 Phase 2: web.dim_target_type is now a real dimension table fetched
// from the API (replacing the old hardcoded TARGET_TYPES array) -- includes
// the corrected "LDAP Directory" display name and the new "Active Directory"
// entry, distinct from the generic "LDAP Directory".
const targetTypes = [
  { targetTypeKey: 1, typeCode: 'Server', displayName: 'Server' },
  { targetTypeKey: 5, typeCode: 'LdapDirectory', displayName: 'LDAP Directory' },
  { targetTypeKey: 6, typeCode: 'ActiveDirectory', displayName: 'Active Directory' }
]

// Mounting triggers: 1) GET /api/applications and GET /api/admin/targets/target-types,
// then 2) load()'s Promise.all([GET /api/admin/targets, GET /api/admin/targets/identifier-types]).
function mockInitialLoad({ targets = [sampleTarget], totalCount = targets.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/applications') return Promise.resolve(jsonResponse([{ applicationKey: 1, applicationCode: 'APP1', applicationName: 'App One' }]))
    if (url === '/api/admin/targets/target-types') return Promise.resolve(jsonResponse(targetTypes))
    if (url.startsWith('/api/admin/targets/identifier-types')) return Promise.resolve(jsonResponse([{ identifierType: 'Hostname', matchPriority: 30, requiresReview: false }]))
    if (url.startsWith('/api/admin/targets')) return Promise.resolve(jsonResponse(targets, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

// D-124 Phase 4: Targets.vue now links "+ New Target"/each row's "Edit" to
// TargetEdit.vue's routes instead of toggling an inline form -- needs a
// real router installed so <router-link> resolves, same pattern
// RiskExceptionsList.test.js already established.
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/targets', name: 'targets', component: { template: '<div />' } },
      { path: '/targets/new', name: 'target-create', component: { template: '<div />' } },
      { path: '/targets/:targetKey', name: 'target-edit', component: { template: '<div />' } },
      { path: '/targets/bulk-import', name: 'targets-bulk-import', component: { template: '<div />' } }
    ]
  })
}

describe('Targets.vue', () => {
  beforeEach(() => {
    // D-124 Phase 3: Targets.vue now reads the shared, server-persisted
    // pageSize store (src/stores/pageSize.js) for pagination -- needs an
    // active Pinia instance, unlike before this phase when the page used
    // no Pinia store at all.
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the target inventory', async () => {
    mockInitialLoad()

    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('web01')
    expect(wrapper.text()).toContain('Hostname=web01.example.com')
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ targets: [sampleTarget], totalCount: 100, filteredCount: 42 })

    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 42 matching (100 total)')
  })

  it('does not show the count summary before the first load resolves its headers', async () => {
    globalThis.fetch = vi.fn(() => new Promise(() => {})) // never resolves
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })

    expect(wrapper.find('.filter-count-summary').exists()).toBe(false)
  })

  // D-124 Phase 3: pagination.
  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const targetsCalls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/targets?'))
    expect(targetsCalls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('shows the pager and disables Prev/Next appropriately on a single-page result', async () => {
    mockInitialLoad({ targets: [sampleTarget], totalCount: 1, filteredCount: 1 })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Page 1 of 1')
    const [prevButton, nextButton] = wrapper.findAll('.pager button')
    expect(prevButton.attributes('disabled')).toBeDefined()
    expect(nextButton.attributes('disabled')).toBeDefined()
  })

  it('clicking Next advances to page 2 and re-fetches with page=2', async () => {
    mockInitialLoad({ targets: [sampleTarget], totalCount: 120, filteredCount: 120 })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()

    const targetsCalls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/targets?'))
    expect(targetsCalls.some(u => u.includes('page=2'))).toBe(true)
    expect(wrapper.text()).toContain('Page 2 of')
  })

  it('changing the page size resets to page 1, persists the preference, and re-fetches with the new pageSize', async () => {
    mockInitialLoad({ targets: [sampleTarget], totalCount: 120, filteredCount: 120 })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    // Advance to page 2 first, so the reset-to-1 behavior is actually exercised.
    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()

    const pageSizeSelect = wrapper.find('.pager select')
    await pageSizeSelect.setValue('100')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/preferences/PageSize', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ value: '100' })
    })
    const targetsCalls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/targets?'))
    expect(targetsCalls.at(-1)).toContain('page=1')
    expect(targetsCalls.at(-1)).toContain('pageSize=100')
  })

  it('sends the Type filter as a targetTypeKey query param', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.get('select').setValue('1') // Server's targetTypeKey in the mocked catalog
    await flushPromises()

    const targetsCalls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/admin/targets?'))
    expect(targetsCalls.some(u => u.includes('targetTypeKey=1'))).toBe(true)
  })

  it('renders the Type dropdown options using DisplayName, not the raw code', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const typeSelect = wrapper.get('select')
    const optionTexts = typeSelect.findAll('option').map(o => o.text())

    expect(optionTexts).toContain('LDAP Directory')
    expect(optionTexts).toContain('Active Directory')
    expect(optionTexts).not.toContain('LdapDirectory')
    expect(optionTexts).not.toContain('ActiveDirectory')
  })

  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // TargetEdit.vue's routed pages -- "+ New Target" and each row's "Edit"
  // are now router-link navigations instead of toggling a form in place.
  it('links "+ New Target" to the target-create route', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const link = wrapper.findAll('a').find(a => a.text() === '+ New Target')
    expect(link.attributes('href')).toBe('/targets/new')
  })

  it('links each row\'s "Edit" to the target-edit route with that row\'s key', async () => {
    mockInitialLoad()
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const link = wrapper.findAll('a').find(a => a.text() === 'Edit')
    expect(link.attributes('href')).toBe(`/targets/${sampleTarget.targetKey}`)
  })

  // D-124 Phase 5: the 2 inline "Bulk Import" sections moved off this page
  // onto TargetsBulkImport.test.js -- coverage here is limited to the
  // "Bulk Actions" link itself, matching how the "+ New Target"/"Edit"
  // router-links are asserted above.
  it('links "Bulk Actions" to the targets-bulk-import route', async () => {
    mockInitialLoad({ targets: [] })
    const wrapper = mount(Targets, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const link = wrapper.findAll('a').find(a => a.text() === 'Bulk Actions')
    expect(link.attributes('href')).toBe('/targets/bulk-import')
  })
})
