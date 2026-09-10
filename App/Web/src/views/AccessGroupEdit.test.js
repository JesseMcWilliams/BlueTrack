import { describe, it, expect, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccessGroupEdit from './AccessGroupEdit.vue'

// D-124 Phase 4: Add/Edit moved off AccessGroups.vue's own inline form onto
// this routed page -- establishes coverage for the create-vs-edit-by-
// route-param branching, including the SOR Type/SOR Address fields that
// AccessGroups.test.js's own removed inline-form tests used to cover.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const sorTypes = [{ sorTypeKey: 1, sorTypeName: 'Domain' }]

const sampleGroup = {
  accessGroupKey: 3,
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

function mockReferenceData() {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/admin/targets') return Promise.resolve(jsonResponse([]))
    if (url === '/api/admin/access-groups/sor-types') return Promise.resolve(jsonResponse(sorTypes))
    if (url === `/api/admin/access-groups/${sampleGroup.accessGroupKey}`) return Promise.resolve(jsonResponse(sampleGroup))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/access-groups', name: 'access-groups', component: { template: '<div />' } },
      { path: '/access-groups/new', name: 'access-group-create', component: { template: '<div />' } },
      { path: '/access-groups/:accessGroupKey', name: 'access-group-edit', component: { template: '<div />' } }
    ]
  })
}

describe('AccessGroupEdit.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('create mode: shows an empty New Access Group form with SOR Type/SOR Address fields available', async () => {
    mockReferenceData()
    const wrapper = mount(AccessGroupEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('New Access Group')
    expect(wrapper.text()).toContain('SOR Type')
    expect(wrapper.text()).toContain('SOR Address')
  })

  it('edit mode: fetches the group by key and pre-fills SOR Type/SOR Address', async () => {
    mockReferenceData()
    const wrapper = mount(AccessGroupEdit, { props: { accessGroupKey: 3 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/access-groups/3')
    expect(wrapper.text()).toContain('Edit Access Group')
    const sorAddressInput = wrapper.findAll('input').find(i => i.element.value === 'company.com')
    expect(sorAddressInput).toBeTruthy()
  })

  it('create mode: POSTs the form and navigates back to the access-groups list on success', async () => {
    mockReferenceData()
    const router = makeRouter()
    const pushSpy = vi.spyOn(router, 'push')
    const wrapper = mount(AccessGroupEdit, { global: { plugins: [router] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({ accessGroupKey: 9 }, { status: 201 }))
    await wrapper.get('input[required]').setValue('New Group')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls.at(-1)
    expect(url).toBe('/api/admin/access-groups')
    expect(options.method).toBe('POST')
    expect(pushSpy).toHaveBeenCalledWith({ name: 'access-groups' })
  })

  it('shows a save error and stays on the page when the save fails', async () => {
    mockReferenceData()
    const wrapper = mount(AccessGroupEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, { ok: false, status: 500 }))
    await wrapper.get('input[required]').setValue('New Group')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('500')
    expect(wrapper.find('form').exists()).toBe(true)
  })
})
