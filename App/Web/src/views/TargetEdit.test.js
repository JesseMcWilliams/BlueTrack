import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import TargetEdit from './TargetEdit.vue'

// D-124 Phase 4: Add/Edit moved off Targets.vue's own inline form onto
// this routed page -- establishes coverage for the create-vs-edit-by-
// route-param branching (mirroring RiskScoreBandEdit.test.js/
// AccessGroupEdit.test.js's own new-this-phase pattern), following
// Targets.test.js's own globalThis.fetch mocking style.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const targetTypes = [
  { targetTypeKey: 1, typeCode: 'Server', displayName: 'Server' }
]
const identifierTypes = [{ identifierType: 'Hostname', matchPriority: 30, requiresReview: false }]

const sampleTarget = {
  targetKey: 5,
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

function mockReferenceData() {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/applications') return Promise.resolve(jsonResponse([]))
    if (url === '/api/admin/targets/identifier-types') return Promise.resolve(jsonResponse(identifierTypes))
    if (url === '/api/admin/targets/target-types') return Promise.resolve(jsonResponse(targetTypes))
    if (url === `/api/admin/targets/${sampleTarget.targetKey}`) return Promise.resolve(jsonResponse(sampleTarget))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/targets', name: 'targets', component: { template: '<div />' } },
      { path: '/targets/new', name: 'target-create', component: { template: '<div />' } },
      { path: '/targets/:targetKey', name: 'target-edit', component: { template: '<div />' } }
    ]
  })
}

describe('TargetEdit.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('create mode: defaults the Type dropdown to the first fetched target type', async () => {
    mockReferenceData()
    const wrapper = mount(TargetEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('New Target')
    expect(wrapper.get('select').element.value).toBe('1')
  })

  it('edit mode: fetches the target by key and pre-fills the form, including identifiers', async () => {
    mockReferenceData()
    const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/targets/5')
    expect(wrapper.text()).toContain('Edit Target')
    expect(wrapper.get('input[required]').element.value).toBe('web01')
    expect(wrapper.text()).toContain('Hostname')
  })

  it('edit mode: shows a load error instead of the form when the target fetch fails', async () => {
    globalThis.fetch = vi.fn((url) => {
      if (url === `/api/admin/targets/${sampleTarget.targetKey}`) return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
      return Promise.resolve(jsonResponse([]))
    })
    const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('404')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('create mode: POSTs the form and navigates back to the targets list on success', async () => {
    mockReferenceData()
    const router = makeRouter()
    const pushSpy = vi.spyOn(router, 'push')
    const wrapper = mount(TargetEdit, { global: { plugins: [router] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({ targetKey: 9 }, { status: 201 }))
    await wrapper.get('input[required]').setValue('new-target')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls.at(-1)
    expect(url).toBe('/api/admin/targets')
    expect(options.method).toBe('POST')
    expect(pushSpy).toHaveBeenCalledWith({ name: 'targets' })
  })

  it('shows a save error and stays on the page when the save fails', async () => {
    mockReferenceData()
    const wrapper = mount(TargetEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, { ok: false, status: 500 }))
    await wrapper.get('input[required]').setValue('new-target')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('500')
    expect(wrapper.find('form').exists()).toBe(true)
  })
})
