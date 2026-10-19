import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import ApiKeyEdit from './ApiKeyEdit.vue'

// D-243: edit an API key in place.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/api-keys', name: 'admin-api-keys', component: { template: '<div />' } }
    ]
  })
}

const key = {
  apiKeyKey: 7, displayName: 'Nightly sync', keyPrefix: 'bt_AbCdEfGh', appRoleKey: 4, roleName: 'Viewer', expiresAt: '2027-03-01T23:59:59',
  isEnabled: true, createdAt: '2026-10-01T10:00:00', createdByName: 'Admin', lastUsedAt: null, lastUsedFrom: null, ownerEmail: 'owner@example.com', rateLimitPerMinute: 75
}
const options = { roles: [{ appRoleKey: 4, roleName: 'Viewer' }, { appRoleKey: 3, roleName: 'Auditor' }], maxLifetimeDays: 365, maxRateLimitPerMinute: 100000 }

async function mountPage({ keyResponse = jsonResponse(key), putResponse = jsonResponse(null, true, 204) } = {}) {
  const router = makeRouter()
  router.push('/')
  await router.isReady()
  globalThis.fetch = vi.fn((url, init) => {
    if (init?.method === 'PUT') return Promise.resolve(putResponse)
    return Promise.resolve(url === '/api/admin/api-keys/options' ? jsonResponse(options) : keyResponse)
  })
  const wrapper = mount(ApiKeyEdit, { props: { apiKeyKey: '7' }, global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

describe('ApiKeyEdit.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the key as it is, without the key itself', async () => {
    const { wrapper } = await mountPage()

    expect(wrapper.get('h2').text()).toBe('Edit API Key')
    expect(wrapper.text()).toContain('bt_AbCdEfGh')
    expect(wrapper.get('input[maxlength="100"]').element.value).toBe('Nightly sync')
    expect(wrapper.get('select').element.value).toBe('4')
    expect(wrapper.get('input[type="date"]').element.value).toBe('2027-03-01')
    expect(wrapper.get('input[type="email"]').element.value).toBe('owner@example.com')
    expect(wrapper.get('input[type="number"]').element.value).toBe('75')
  })

  it('saves a changed role, owner and limit, and sends no expiry when the date is untouched', async () => {
    const { wrapper, router } = await mountPage()

    await wrapper.findAll('option')[1].setSelected()
    await wrapper.get('input[type="email"]').setValue('new@example.com')
    await wrapper.get('input[type="number"]').setValue('')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(put[0]).toBe('/api/admin/api-keys/7')
    expect(JSON.parse(put[1].body)).toEqual({ displayName: 'Nightly sync', appRoleKey: 3, expiresOn: null, ownerEmail: 'new@example.com', rateLimitPerMinute: null })
    expect(router.currentRoute.value.name).toBe('admin-api-keys')
  })

  it('sends a changed expiry date', async () => {
    const { wrapper } = await mountPage()

    await wrapper.get('input[type="date"]').setValue('2027-05-05')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(JSON.parse(put[1].body).expiresOn).toBe('2027-05-05')
  })

  it('clears the owner when the field is emptied', async () => {
    const { wrapper } = await mountPage()

    await wrapper.get('input[type="email"]').setValue('')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(JSON.parse(put[1].body).ownerEmail).toBeNull()
  })

  it('shows why a save was refused and stays on the page', async () => {
    const { wrapper, router } = await mountPage({ putResponse: jsonResponse({ error: "There is already an API key named 'x'." }, false, 409) })

    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('already an API key')
    expect(router.currentRoute.value.name).toBe('home')
  })

  it('says so when the key is gone', async () => {
    const { wrapper } = await mountPage({ keyResponse: jsonResponse(null, false, 404) })
    expect(wrapper.get('[role="alert"]').text()).toContain('no longer exists')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('Cancel goes back to the list without saving', async () => {
    const { wrapper, router } = await mountPage()
    await wrapper.findAll('button').find(b => b.text() === 'Cancel').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('admin-api-keys')
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  })

  // UI review W10-6: the date input had min=today, so the browser refused to save an expired key even for a rename.
  describe('an expired key', () => {
    const expired = { ...key, expiresAt: '2020-01-15T23:59:59' }

    it('can be saved untouched: the date input has no minimum, and says the key expired', async () => {
      const { wrapper } = await mountPage({ keyResponse: jsonResponse(expired) })

      const date = wrapper.get('input[type="date"]')
      expect(date.attributes('min')).toBeUndefined()
      expect(wrapper.text()).toContain('This key expired on 2020-01-15.')
    })

    it('sends no expiry when only the name changed', async () => {
      const { wrapper } = await mountPage({ keyResponse: jsonResponse(expired) })
      await wrapper.get('input[maxlength="100"]').setValue('Renamed')

      await wrapper.get('form').trigger('submit')
      await flushPromises()

      const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
      expect(JSON.parse(put[1].body)).toMatchObject({ displayName: 'Renamed', expiresOn: null })
    })

    it('requires today or later once a new date is being chosen', async () => {
      const { wrapper } = await mountPage({ keyResponse: jsonResponse(expired) })

      await wrapper.get('input[type="date"]').setValue('2020-02-01')

      expect(wrapper.get('input[type="date"]').attributes('min')).toBe(new Date().toISOString().slice(0, 10))
    })
  })

  it('a key that has not expired has no expired note, and a changed date must be today or later', async () => {
    const { wrapper } = await mountPage()

    expect(wrapper.text()).not.toContain('expired on')
    await wrapper.get('input[type="date"]').setValue('2020-02-01')
    expect(wrapper.get('input[type="date"]').attributes('min')).toBe(new Date().toISOString().slice(0, 10))
  })
})
