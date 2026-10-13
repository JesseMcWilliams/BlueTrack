import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import ApplicationSafeMapping from './ApplicationSafeMapping.vue'

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
})
