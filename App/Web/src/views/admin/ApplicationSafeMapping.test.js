import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import ApplicationSafeMapping from './ApplicationSafeMapping.vue'

vi.mock('../../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../../composables/useConfirmDialog'

// D-213: both tables page server-side at the user's page size; the Safes
// rows' select still offers every application.

function jsonResponse(body, count = null) {
  return {
    ok: true,
    status: 200,
    headers: { get: (name) => (name === 'X-Total-Count' || name === 'X-Filtered-Count') && count !== null ? String(count) : null },
    json: () => Promise.resolve(body)
  }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/application-mapping/:applicationKey', name: 'admin-application-edit', component: { template: '<div />' } },
      { path: '/admin/application-mapping/new', name: 'admin-application-create', component: { template: '<div />' } },
      { path: '/admin/application-mapping/bulk', name: 'admin-application-mapping-bulk-import', component: { template: '<div />' } }
    ]
  })
}

const app = (key) => ({ applicationKey: key, applicationCode: `APP${key}`, applicationName: `App ${key}`, ownerName: null })
const safe = (key) => ({ safeKey: key, safeName: `Safe${key}`, applicationKey: null })

describe('ApplicationSafeMapping.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    globalThis.fetch = vi.fn((url) => {
      if (url === '/api/applications/detailed') return Promise.resolve(jsonResponse([app(1), app(2), app(3)]))
      if (url.startsWith('/api/applications/detailed?')) return Promise.resolve(jsonResponse([app(1)], 120))
      if (url.startsWith('/api/safes?')) return Promise.resolve(jsonResponse([safe(1)], 120))
      return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve(null) })
    })
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads one page of each table, and every application for the select', async () => {
    const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const urls = globalThis.fetch.mock.calls.map(c => c[0])
    expect(urls).toContain('/api/applications/detailed?page=1&pageSize=50')
    expect(urls).toContain('/api/safes?page=1&pageSize=50')
    expect(urls).toContain('/api/applications/detailed')
    expect(wrapper.findAll('.applications-table tbody tr')).toHaveLength(1)
    expect(wrapper.get('select[aria-label="Application for safe Safe1"]').findAll('option')).toHaveLength(4)
  })

  it('the Safes pager moves only the Safes table', async () => {
    const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    globalThis.fetch.mockClear()

    const nextButtons = wrapper.findAll('button').filter(b => /next/i.test(b.text()))
    await nextButtons.at(-1).trigger('click')
    await flushPromises()

    const urls = globalThis.fetch.mock.calls.map(c => c[0])
    expect(urls).toEqual(['/api/safes?page=2&pageSize=50'])
  })

  // UI review W8-1: a failed assignment left the select on the value that was not saved, with the error only at the
  // top of the page; a successful one said nothing.
  describe('assigning a Safe to an application', () => {
    function mockAssign(putResponse) {
      let saved = null
      globalThis.fetch = vi.fn((url, init) => {
        if (init?.method === 'PUT') return putResponse instanceof Error ? Promise.reject(putResponse) : Promise.resolve(putResponse)
        if (url === '/api/applications/detailed') return Promise.resolve(jsonResponse([app(1), app(2), app(3)]))
        if (url.startsWith('/api/applications/detailed?')) return Promise.resolve(jsonResponse([app(1)], 1))
        if (url.startsWith('/api/safes?')) return Promise.resolve(jsonResponse([{ ...safe(1), applicationKey: saved }], 1))
        return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve(null) })
      })
      return (value) => { saved = value }
    }

    async function pick(wrapper, value) {
      const select = wrapper.get('select[aria-label="Application for safe Safe1"]')
      select.element.value = String(value)
      await select.trigger('change')
      await flushPromises()
      return select
    }

    it('says what was assigned, next to the Safes table', async () => {
      mockAssign({ ok: true, status: 204, json: () => Promise.resolve(null) })
      const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
      await flushPromises()

      await pick(wrapper, 2)

      expect(wrapper.find('.assign-notice').text()).toBe('Safe1 is now assigned to App 2.')
      expect(wrapper.find('.assign-error').exists()).toBe(false)
      const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
      expect(put[0]).toBe('/api/safes/1/application')
      expect(put[1].body).toBe('2')
    })

    it('says so when the Safe is unassigned', async () => {
      mockAssign({ ok: true, status: 204, json: () => Promise.resolve(null) })
      const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
      await flushPromises()

      await pick(wrapper, '')

      expect(wrapper.find('.assign-notice').text()).toBe('Safe1 no longer has an application.')
    })

    it('a refused assignment shows the reason and puts the select back on what is saved', async () => {
      mockAssign({ ok: false, status: 403, json: () => Promise.resolve({ detail: 'You cannot change this Safe.' }) })
      const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
      await flushPromises()

      const select = await pick(wrapper, 2)

      expect(wrapper.find('.assign-error').text()).toContain('wasn\'t changed: You cannot change this Safe.')
      expect(select.element.value).toBe('')   // reloaded: nothing was saved
    })

    it('a refusal with no reason gives the status', async () => {
      mockAssign({ ok: false, status: 500, json: () => Promise.reject(new Error('no body')) })
      const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
      await flushPromises()

      await pick(wrapper, 2)

      expect(wrapper.find('.assign-error').text()).toContain('Request failed: 500')
    })

    it('no answer at all is reported, and the select goes back', async () => {
      mockAssign(new TypeError('Failed to fetch'))
      const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
      await flushPromises()

      const select = await pick(wrapper, 3)

      expect(wrapper.find('.assign-error').text()).toContain("the server didn't answer")
      expect(select.element.value).toBe('')
    })

    it('the next successful assignment clears the earlier error', async () => {
      const setSaved = mockAssign({ ok: false, status: 500, json: () => Promise.reject(new Error('x')) })
      const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
      await flushPromises()
      await pick(wrapper, 2)
      expect(wrapper.find('.assign-error').exists()).toBe(true)

      setSaved(2)
      globalThis.fetch.mockImplementation((url, init) => {
        if (init?.method === 'PUT') return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) })
        if (url === '/api/applications/detailed') return Promise.resolve(jsonResponse([app(1), app(2), app(3)]))
        if (url.startsWith('/api/applications/detailed?')) return Promise.resolve(jsonResponse([app(1)], 1))
        return Promise.resolve(jsonResponse([{ ...safe(1), applicationKey: 2 }], 1))
      })
      await pick(wrapper, 2)

      expect(wrapper.find('.assign-error').exists()).toBe(false)
      expect(wrapper.find('.assign-notice').text()).toContain('is now assigned to App 2')
    })
  })
})

describe('ApplicationSafeMapping.vue: usage, delete and Safe search (UI review W8-2, W8-5, W8-6)', () => {
  const used = { ...app(1), safeCount: 2, accountCount: 5, targetCount: 0, exceptionCount: 1 }
  const unused = { ...app(2), safeCount: 0, accountCount: 0, targetCount: 0, exceptionCount: 0 }

  beforeEach(() => {
    setActivePinia(createPinia())
    confirmDelete.mockResolvedValue(true)
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  function mockApi({ onDelete } = {}) {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'DELETE') return Promise.resolve(onDelete ?? { ok: true, status: 204, json: () => Promise.resolve(null) })
      if (url === '/api/applications/detailed') return Promise.resolve(jsonResponse([used, unused]))
      if (url.startsWith('/api/applications/detailed?')) return Promise.resolve(jsonResponse([used, unused], 2))
      if (url.startsWith('/api/safes?')) return Promise.resolve(jsonResponse([safe(1)], 1))
      return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve(null) })
    })
  }

  it('shows what uses each application and only lets an unused one be deleted', async () => {
    mockApi()
    const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const rows = wrapper.findAll('.applications-table tbody tr')
    expect(rows[0].text()).toContain('2 Safes, 5 accounts, 0 targets, 1 exceptions')
    expect(rows[0].find('button').attributes('disabled')).toBeDefined()
    expect(rows[1].find('button').attributes('disabled')).toBeUndefined()

    await rows[1].find('button').trigger('click')
    await flushPromises()
    expect(confirmDelete).toHaveBeenCalledWith('Delete the application "App 2"? Nothing uses it.')
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/applications/2' && init?.method === 'DELETE')).toBe(true)
  })

  it("shows the API's reason when a delete is refused, and does nothing when cancelled", async () => {
    mockApi({ onDelete: { ok: false, status: 409, json: () => Promise.resolve({ detail: 'It is still used by a Safe.' }) } })
    const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('.applications-table tbody tr')[1].find('button').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toBe('It is still used by a Safe.')

    globalThis.fetch.mockClear()
    confirmDelete.mockResolvedValueOnce(false)
    await wrapper.findAll('.applications-table tbody tr')[1].find('button').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
  })

  it('searches the Safes by name and can narrow them to those with no application', async () => {
    vi.useFakeTimers()
    mockApi()
    const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
    await vi.advanceTimersByTimeAsync(0)
    await flushPromises()
    globalThis.fetch.mockClear()

    await wrapper.find('input[type="search"]').setValue('prod')
    await wrapper.findAll('input[type="checkbox"]').at(-1).setValue(true)
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    const safeCalls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/safes?'))
    expect(safeCalls).toEqual(['/api/safes?page=1&pageSize=50&search=prod&unassigned=true'])
  })

  it('explains the four ways an application gets onto an account', async () => {
    mockApi()
    const wrapper = mount(ApplicationSafeMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    expect(wrapper.find('.intro').text()).toContain('four ways')
  })
})
