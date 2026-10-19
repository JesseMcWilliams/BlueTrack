import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SecretsStoreConfiguration from './SecretsStoreConfiguration.vue'

// UI review W10-1: making a backend active switches where secrets are looked up, with no question and no message.
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => (body === undefined ? Promise.reject(new Error('no body')) : Promise.resolve(body)) }
}

function backends(active) {
  return ['WindowsDpapi', 'CyberArkCP', 'AzureKeyVault'].map((backendType, i) => ({
    secretStoreKey: i + 1, backendType, isActive: backendType === active, backendSettings: backendType === 'CyberArkCP' ? JSON.stringify({ AppId: 'APP_Test' }) : null
  }))
}

function mockApi({ active = 'WindowsDpapi', putResponse = jsonResponse(null, true, 204) } = {}) {
  let current = active
  globalThis.fetch = vi.fn((url, init) => {
    if (url === '/api/admin/secrets-store' && !init?.method) return Promise.resolve(jsonResponse(backends(current)))
    if (url === '/api/admin/secrets-store/active' && init?.method === 'PUT') {
      if (putResponse.ok) current = JSON.parse(init.body).backendType
      return Promise.resolve(putResponse)
    }
    return Promise.resolve(jsonResponse(null, false, 404))
  })
}

async function mountPage() {
  const wrapper = mount(SecretsStoreConfiguration, { global: { stubs: { 'router-link': true } } })
  await flushPromises()
  return wrapper
}

const rowFor = (wrapper, type) => wrapper.findAll('tbody tr').find(r => r.find('td').text() === type)
const puts = () => globalThis.fetch.mock.calls.filter(([, init]) => init?.method === 'PUT')

describe('SecretsStoreConfiguration.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('Make active asks first, naming the backend, and changes nothing when declined', async () => {
    confirmDelete.mockResolvedValue(false)
    mockApi()
    const wrapper = await mountPage()

    await rowFor(wrapper, 'CyberArkCP').find('button').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledTimes(1)
    const [message, label] = confirmDelete.mock.calls[0]
    expect(message.title).toBe('Make CyberArkCP the active backend?')
    expect(message.details).toEqual(['The application will look secrets up in CyberArkCP from now on.'])
    expect(message.warning).toContain("those lookups fail until you make another backend active")
    expect(label).toBe('Make Active')
    expect(puts()).toHaveLength(0)
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('a confirmed Make active switches the backend and says so', async () => {
    mockApi()
    const wrapper = await mountPage()

    await rowFor(wrapper, 'CyberArkCP').find('button').trigger('click')
    await flushPromises()

    expect(JSON.parse(puts()[0][1].body).backendType).toBe('CyberArkCP')
    expect(wrapper.text()).toContain('CyberArkCP is now the active backend.')
    expect(rowFor(wrapper, 'CyberArkCP').find('button').text()).toBe('Save')   // it is the active one now
  })

  it('saving the backend that is already active asks nothing, and says the settings were saved', async () => {
    mockApi({ active: 'CyberArkCP' })
    const wrapper = await mountPage()

    await rowFor(wrapper, 'CyberArkCP').find('button').trigger('click')
    await flushPromises()

    expect(confirmDelete).not.toHaveBeenCalled()
    expect(puts()).toHaveLength(1)
    expect(wrapper.text()).toContain('CyberArkCP settings saved.')
    expect(wrapper.text()).not.toContain('is now the active backend')
  })

  it("a refused switch shows the API's reason and no success message", async () => {
    mockApi({ putResponse: jsonResponse({ detail: 'Azure Key Vault needs a vault URL.' }, false, 400) })
    const wrapper = await mountPage()

    await rowFor(wrapper, 'AzureKeyVault').find('button').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('Azure Key Vault needs a vault URL.')
    expect(wrapper.text()).not.toContain('is now the active backend')
  })

  it('a refusal with no reason shows the status', async () => {
    mockApi({ putResponse: jsonResponse(undefined, false, 500) })
    const wrapper = await mountPage()

    await rowFor(wrapper, 'CyberArkCP').find('button').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('500')
  })

  it('an earlier message does not stay once the next action starts', async () => {
    mockApi()
    const wrapper = await mountPage()
    await rowFor(wrapper, 'CyberArkCP').find('button').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('is now the active backend')

    confirmDelete.mockResolvedValue(false)
    await rowFor(wrapper, 'AzureKeyVault').find('button').trigger('click')
    await flushPromises()

    expect(wrapper.text()).not.toContain('is now the active backend')
  })
})
