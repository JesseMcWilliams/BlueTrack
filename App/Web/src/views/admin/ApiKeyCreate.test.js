import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import ApiKeyCreate from './ApiKeyCreate.vue'

// D-243: New API Key. The key is shown once, after the create.
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

const options = {
  roles: [{ appRoleKey: 4, roleName: 'Viewer' }, { appRoleKey: 3, roleName: 'Auditor' }],
  defaultLifetimeDays: 90,
  maxLifetimeDays: 365
}

async function mountPage(createResponse = jsonResponse({ apiKeyKey: 9, apiKey: 'bt_SECRETSECRETSECRET', keyPrefix: 'bt_SECRETSE', expiresAt: '2027-01-15T21:59:02Z' }, true, 201)) {
  const router = makeRouter()
  router.push('/')
  await router.isReady()
  globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST' ? createResponse : jsonResponse(options)))
  const wrapper = mount(ApiKeyCreate, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

describe('ApiKeyCreate.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('offers the roles it was given (no Admin) and the default lifetime', async () => {
    const { wrapper } = await mountPage()

    expect(wrapper.get('h2').text()).toBe('New API Key')
    expect(wrapper.findAll('option').map(o => o.text())).toEqual(['Viewer', 'Auditor'])
    expect(wrapper.get('input[type="number"]').element.value).toBe('90')
    expect(wrapper.get('input[type="number"]').attributes('max')).toBe('365')
  })

  it('creates the key, then shows it once with a copy button', async () => {
    const { wrapper } = await mountPage()

    await wrapper.get('input[maxlength="100"]').setValue('Nightly sync')
    await wrapper.findAll('option')[1].setSelected()
    await wrapper.get('input[type="number"]').setValue('30')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post[0]).toBe('/api/admin/api-keys')
    expect(JSON.parse(post[1].body)).toEqual({ displayName: 'Nightly sync', appRoleKey: 3, lifetimeDays: 30 })
    expect(wrapper.get('input.api-key-value').element.value).toBe('bt_SECRETSECRETSECRET')
    expect(wrapper.text()).toContain('shown only once')
    expect(wrapper.text()).toContain('It expires on 2027-01-15.')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('copies the key to the clipboard', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(globalThis.navigator, 'clipboard', { value: { writeText }, configurable: true })
    const { wrapper } = await mountPage()
    await wrapper.get('input[maxlength="100"]').setValue('x')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Copy').trigger('click')
    await flushPromises()

    expect(writeText).toHaveBeenCalledWith('bt_SECRETSECRETSECRET')
    expect(wrapper.text()).toContain('Copied.')
  })

  it('Done goes back to the list', async () => {
    const { wrapper, router } = await mountPage()
    await wrapper.get('input[maxlength="100"]').setValue('x')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Done').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('admin-api-keys')
  })

  it('shows why a create was refused and keeps the form', async () => {
    const { wrapper } = await mountPage(jsonResponse({ error: "There is already an API key named 'x'." }, false, 409))
    await wrapper.get('input[maxlength="100"]').setValue('x')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('already an API key')
    expect(wrapper.find('form').exists()).toBe(true)
  })
})
