import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import ApiKeys from './ApiKeys.vue'

// D-243: Admin > API Keys.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/api-keys/new', name: 'admin-api-key-create', component: { template: '<div />' } },
      { path: '/admin/api-keys/:apiKeyKey', name: 'admin-api-key-edit', component: { template: '<div />' } }
    ]
  })
}

const future = new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 19)
const past = new Date(Date.now() - 86400000).toISOString().slice(0, 19)

const keys = [
  { apiKeyKey: 1, displayName: 'Nightly sync', keyPrefix: 'bt_AbCdEfGh', appRoleKey: 4, roleName: 'Viewer', expiresAt: future, isEnabled: true, createdAt: '2026-10-17T10:00:00', createdByName: 'Admin', lastUsedAt: '2026-10-17T12:30:15', lastUsedFrom: '10.0.0.5', ownerEmail: 'owner@example.com', rateLimitPerMinute: null },
  { apiKeyKey: 2, displayName: 'Old report script', keyPrefix: 'bt_ZyXwVuTs', appRoleKey: 3, roleName: 'Auditor', expiresAt: future, isEnabled: false, createdAt: '2026-10-01T10:00:00', createdByName: 'Admin', lastUsedAt: null, lastUsedFrom: null },
  { apiKeyKey: 3, displayName: 'Expired one', keyPrefix: 'bt_QqWwEeRr', appRoleKey: 4, roleName: 'Viewer', expiresAt: past, isEnabled: true, createdAt: '2026-07-01T10:00:00', createdByName: null, lastUsedAt: null, lastUsedFrom: null }
]

// The client mappings section loads its own two lists; these tests are about the keys.
function route(keysResponse) {
  return (url, init) => {
    if (init?.method) return Promise.resolve(jsonResponse(null, true, 204))
    if (url === '/api/admin/api-client-mappings/options') return Promise.resolve(jsonResponse({ providers: [], roles: [], matchTypes: [] }))
    if (url === '/api/admin/api-client-mappings') return Promise.resolve(jsonResponse([]))
    return Promise.resolve(keysResponse)
  }
}

async function mountPage(response = jsonResponse(keys)) {
  const router = makeRouter()
  router.push('/')
  await router.isReady()
  globalThis.fetch = vi.fn(route(response))
  const wrapper = mount(ApiKeys, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

describe('ApiKeys.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists each key with its prefix, role and status, and never a full key', async () => {
    const { wrapper } = await mountPage()

    const text = wrapper.text()
    expect(wrapper.get('h2').text()).toBe('API Keys')
    expect(text).toContain('Nightly sync')
    expect(text).toContain('bt_AbCdEfGh')
    expect(text).toContain('Viewer')
    expect(text).toContain('Auditor')
    expect(text).toContain('owner@example.com')
    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].text()).toContain('Enabled')
    expect(rows[0].text()).toContain('from 10.0.0.5')
    expect(rows[1].text()).toContain('Disabled')
    expect(rows[1].text()).toContain('Never')
    expect(rows[2].text()).toContain('Expired')
  })

  it('says so when there are no keys', async () => {
    const { wrapper } = await mountPage(jsonResponse([]))
    expect(wrapper.text()).toContain('There are no API keys yet.')
    expect(wrapper.find('table').exists()).toBe(false)
  })

  it('the key name opens its edit page', async () => {
    const { wrapper, router } = await mountPage()
    await wrapper.findAll('tbody tr')[0].get('a').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('admin-api-key-edit')
    expect(router.currentRoute.value.params.apiKeyKey).toBe('1')
  })

  it('New API Key opens the create page', async () => {
    const { wrapper, router } = await mountPage()
    await wrapper.findAll('button').find(b => b.text() === 'New API Key').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('admin-api-key-create')
  })

  it('Disable and Enable call the key and reload', async () => {
    const { wrapper } = await mountPage()
    const rows = wrapper.findAll('tbody tr')

    await rows[0].findAll('button').find(b => b.text() === 'Disable').trigger('click')
    await flushPromises()
    await rows[1].findAll('button').find(b => b.text() === 'Enable').trigger('click')
    await flushPromises()

    const posts = globalThis.fetch.mock.calls.filter(([, init]) => init?.method === 'POST').map(([url]) => url)
    expect(posts).toEqual(['/api/admin/api-keys/1/disable', '/api/admin/api-keys/2/enable'])
  })

  it('Delete asks first, then deletes; a refusal deletes nothing', async () => {
    const { wrapper } = await mountPage()
    const deleteButton = () => wrapper.findAll('tbody tr')[0].findAll('button').find(b => b.text() === 'Delete')

    confirmDelete.mockResolvedValueOnce(false)
    await deleteButton().trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)

    await deleteButton().trigger('click')
    await flushPromises()
    expect(confirmDelete).toHaveBeenLastCalledWith('Delete the API key "Nightly sync"? Scripts that use it stop working at once.', 'Delete')
    const call = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'DELETE')
    expect(call[0]).toBe('/api/admin/api-keys/1')
  })

  it('Rotate does nothing when the confirmation is cancelled', async () => {
    const { wrapper } = await mountPage()
    confirmDelete.mockResolvedValueOnce(false)
    globalThis.fetch.mockClear()

    await wrapper.findAll('tbody tr')[0].findAll('button').find(b => b.text() === 'Rotate').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).not.toHaveBeenCalled()
  })

  it('Rotate makes a new key with the same role, shows it once, and Done returns to the list', async () => {
    const rotatedBody = { apiKeyKey: 9, apiKey: 'bt_ROTATEDKEYROTATEDKEY', keyPrefix: 'bt_ROTATEDK', expiresAt: '2027-01-15T21:59:02Z', displayName: 'Nightly sync (rotated 2026-10-17)', rotatedFrom: 'Nightly sync' }
    const { wrapper } = await mountPage()
    globalThis.fetch = vi.fn((url, init) => init?.method === 'POST' ? Promise.resolve(jsonResponse(rotatedBody, true, 201)) : route(jsonResponse(keys))(url, init))

    await wrapper.findAll('tbody tr')[0].findAll('button').find(b => b.text() === 'Rotate').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenLastCalledWith(expect.stringContaining('Rotate the API key "Nightly sync"?'), 'Rotate')
    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post[0]).toBe('/api/admin/api-keys/1/rotate')
    expect(wrapper.get('input.api-key-value').element.value).toBe('bt_ROTATEDKEYROTATEDKEY')
    expect(wrapper.text()).toContain('A new API key "Nightly sync (rotated 2026-10-17)" was created from "Nightly sync".')
    expect(wrapper.find('table').exists()).toBe(false)

    await wrapper.findAll('button').find(b => b.text() === 'Done').trigger('click')
    await flushPromises()
    expect(wrapper.find('table').exists()).toBe(true)
    expect(wrapper.find('input.api-key-value').exists()).toBe(false)
  })

  it('shows a failure to load', async () => {
    const { wrapper } = await mountPage(jsonResponse(null, false, 500))
    expect(wrapper.find('[role="alert"]').text()).toContain('500')
  })
})
