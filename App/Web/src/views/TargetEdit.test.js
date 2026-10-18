import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import TargetEdit from './TargetEdit.vue'

vi.mock('../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../composables/useConfirmDialog'

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

  // D-229: a primary lists its linked targets; a linked target shows its primary; both offer Unlink.
  function mockLinked(target, afterUnlink) {
    let unlinked = false
    globalThis.fetch = vi.fn((url, init) => {
      if (url === '/api/applications') return Promise.resolve(jsonResponse([]))
      if (url === '/api/admin/targets/identifier-types') return Promise.resolve(jsonResponse(identifierTypes))
      if (url === '/api/admin/targets/target-types') return Promise.resolve(jsonResponse(targetTypes))
      if (url.endsWith('/unlink') && init?.method === 'POST') { unlinked = true; return Promise.resolve(jsonResponse(null, { status: 204 })) }
      if (url === `/api/admin/targets/${target.targetKey}`) return Promise.resolve(jsonResponse(unlinked ? afterUnlink : target))
      return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
    })
  }

  it('lists the targets linked to this one and unlinks one after asking', async () => {
    confirmDelete.mockResolvedValue(true)
    const linked = { targetKey: 8, targetName: 'db01', targetTypeDisplayName: 'Database', discoverySource: 'CMDB', linkedDate: '2026-10-14T10:00:00' }
    mockLinked({ ...sampleTarget, linkedTargets: [linked] }, { ...sampleTarget, linkedTargets: [] })
    const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Linked Targets')
    expect(wrapper.text()).toContain('CMDB')
    expect(wrapper.get('a[href="/targets/8"]').text()).toBe('db01')

    await wrapper.get('button[aria-label="Unlink db01"]').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.objectContaining({ title: 'Unlink Target', details: ['db01 from web01'] }), 'Unlink')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/targets/8/unlink', { method: 'POST' })
    expect(wrapper.text()).not.toContain('Linked Targets')
  })

  it('shows the target a linked target is linked to, and does nothing when Unlink is cancelled', async () => {
    confirmDelete.mockResolvedValue(false)
    mockLinked({ ...sampleTarget, linkedToTargetKey: 3, linkedToTargetName: 'primary01', linkedTargets: [] }, sampleTarget)
    const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Linked Target')
    expect(wrapper.get('a[href="/targets/3"]').text()).toBe('primary01')

    await wrapper.findAll('button').find(b => b.text() === 'Unlink').trigger('click')
    await flushPromises()

    expect(globalThis.fetch.mock.calls.some(([url]) => url.endsWith('/unlink'))).toBe(false)
  })

  it('does not send the link fields back when saving', async () => {
    mockLinked({ ...sampleTarget, linkedToTargetKey: 3, linkedToTargetName: 'primary01', linkedTargets: [] }, sampleTarget)
    const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, { status: 204 }))
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const [, options] = globalThis.fetch.mock.calls.at(-1)
    expect(options.method).toBe('PUT')
    expect(JSON.parse(options.body)).not.toHaveProperty('linkedToTargetKey')
  })

  // D-245: link this target to another by hand.
  function mockLinkable({ linkResponse = jsonResponse(null, { status: 204 }) } = {}) {
    let linked = false
    globalThis.fetch = vi.fn((url, init) => {
      if (url === '/api/applications') return Promise.resolve(jsonResponse([]))
      if (url === '/api/admin/targets/identifier-types') return Promise.resolve(jsonResponse(identifierTypes))
      if (url === '/api/admin/targets/target-types') return Promise.resolve(jsonResponse(targetTypes))
      if (url.startsWith('/api/admin/targets/search')) {
        return Promise.resolve(jsonResponse([
          { targetKey: 5, targetName: 'web01', targetTypeDisplayName: 'Server', identifiers: [] },
          { targetKey: 3, targetName: 'primary01', targetTypeDisplayName: 'Server', identifiers: [{ identifierType: 'Hostname', identifierValue: 'primary01.example.com' }] }
        ]))
      }
      if (url === '/api/admin/targets/5/link' && init?.method === 'POST') { if (linkResponse.ok) linked = true; return Promise.resolve(linkResponse) }
      if (url === '/api/admin/targets/5') {
        return Promise.resolve(jsonResponse(linked ? { ...sampleTarget, linkedToTargetKey: 3, linkedToTargetName: 'primary01', linkedTargets: [] } : { ...sampleTarget, linkedTargets: [] }))
      }
      return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
    })
  }

  async function chooseLinkTarget(wrapper, text, optionName) {
    const input = wrapper.get('input[role="combobox"]')
    await input.setValue(text)
    vi.advanceTimersByTime(250)
    await flushPromises()
    const options = wrapper.findAll('[role="option"]')
    await options.find(o => o.text().includes(optionName)).trigger('click')
    await flushPromises()
  }

  it('offers Link to another target, leaves this target out of the choices, and links to the chosen one after asking', async () => {
    vi.useFakeTimers()
    try {
      confirmDelete.mockResolvedValue(true)
      mockLinkable()
      const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
      await flushPromises()

      expect(wrapper.text()).toContain('Link to another target')
      expect(wrapper.findAll('button').find(b => b.text() === 'Link').attributes('disabled')).toBeDefined()   // nothing chosen yet

      await chooseLinkTarget(wrapper, 'pri', 'primary01')
      const optionTexts = wrapper.findAll('[role="option"]').map(o => o.text())
      expect(optionTexts.some(t => t.includes('web01'))).toBe(false)

      await wrapper.findAll('button').find(b => b.text() === 'Link').trigger('click')
      await flushPromises()

      expect(confirmDelete).toHaveBeenCalledWith(expect.objectContaining({ title: 'Link this target?', details: ['"web01" will be linked to "primary01".'] }), 'Link')
      const post = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/targets/5/link' && init?.method === 'POST')
      expect(JSON.parse(post[1].body)).toEqual({ primaryTargetKey: 3 })
      // The page now shows the link (and no longer offers one).
      expect(wrapper.text()).toContain('Linked Target')
      expect(wrapper.get('a[href="/targets/3"]').text()).toBe('primary01')
      expect(wrapper.text()).not.toContain('Link to another target')
    } finally {
      vi.useRealTimers()
    }
  })

  it('does nothing when the link is not confirmed, and shows why a link was refused', async () => {
    vi.useFakeTimers()
    try {
      mockLinkable({ linkResponse: jsonResponse({ detail: "'web01' is already linked to another target. Unlink it first." }, { ok: false, status: 409 }) })
      const wrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
      await flushPromises()
      await chooseLinkTarget(wrapper, 'pri', 'primary01')

      confirmDelete.mockResolvedValueOnce(false)
      await wrapper.findAll('button').find(b => b.text() === 'Link').trigger('click')
      await flushPromises()
      expect(globalThis.fetch.mock.calls.some(([url]) => url === '/api/admin/targets/5/link')).toBe(false)

      confirmDelete.mockResolvedValueOnce(true)
      await wrapper.findAll('button').find(b => b.text() === 'Link').trigger('click')
      await flushPromises()
      expect(wrapper.find('[role="alert"]').text()).toContain('Unlink it first')
      expect(wrapper.text()).toContain('Link to another target')
    } finally {
      vi.useRealTimers()
    }
  })

  it('does not offer to link a target that is linked, or that has targets linked to it, or a new one', async () => {
    mockLinked({ ...sampleTarget, linkedToTargetKey: 3, linkedToTargetName: 'primary01', linkedTargets: [] }, sampleTarget)
    const linkedWrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()
    expect(linkedWrapper.text()).not.toContain('Link to another target')

    const linked = { targetKey: 8, targetName: 'db01', targetTypeDisplayName: 'Database', discoverySource: 'CMDB', linkedDate: '2026-10-14T10:00:00' }
    mockLinked({ ...sampleTarget, linkedTargets: [linked] }, sampleTarget)
    const primaryWrapper = mount(TargetEdit, { props: { targetKey: 5 }, global: { plugins: [makeRouter()] } })
    await flushPromises()
    expect(primaryWrapper.text()).not.toContain('Link to another target')

    mockReferenceData()
    const createWrapper = mount(TargetEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    expect(createWrapper.text()).not.toContain('Link to another target')
  })
})
