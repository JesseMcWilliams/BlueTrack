import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import IdentityProviders from './IdentityProviders.vue'
import IdentityProviderEdit from './IdentityProviderEdit.vue'

vi.mock('../../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../../composables/useConfirmDialog'

// UI review W1-5/W10-2 (the OIDC restart note) and W1-7/W10-3 (deleting a provider that is the last or the default).
const stub = { template: '<div />' }
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: stub },
      { path: '/idp/new', name: 'admin-identity-provider-create', component: stub },
      { path: '/idp/:providerKey', name: 'admin-identity-provider-edit', component: stub }
    ]
  })
}
function json(body) { return { ok: true, status: 200, json: () => Promise.resolve(body) } }

async function mountList(providers, defaultType = null) {
  globalThis.fetch = vi.fn((url, init) => {
    if (init?.method === 'DELETE') return Promise.resolve({ ok: true, status: 204 })
    if (url.endsWith('/default')) return Promise.resolve(json({ providerType: defaultType }))
    return Promise.resolve(json(providers.map(p => ({ ...p }))))
  })
  const wrapper = mount(IdentityProviders, { global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

const oidc = { providerKey: 1, providerType: 'OIDC', displayName: 'Corporate SSO', isEnabled: true }
const saml = { providerKey: 2, providerType: 'SAML', displayName: 'Backup SAML', isEnabled: true }
const off = { providerKey: 3, providerType: 'SAML', displayName: 'Old SAML', isEnabled: false }

describe('Identity provider safeguards', () => {
  beforeEach(() => confirmDelete.mockResolvedValue(true))
  afterEach(() => vi.restoreAllMocks())

  it('deleting one of several enabled providers asks the plain question', async () => {
    const wrapper = await mountList([oidc, saml])
    await wrapper.find('button[aria-label="Delete Corporate SSO"]').trigger('click')
    await flushPromises()
    expect(typeof confirmDelete.mock.calls.at(-1)[0]).toBe('string')
  })

  it('warns when the provider is the only enabled one', async () => {
    const wrapper = await mountList([oidc, off])
    await wrapper.find('button[aria-label="Delete Corporate SSO"]').trigger('click')
    await flushPromises()
    expect(confirmDelete.mock.calls.at(-1)[0].warning).toContain('only enabled provider')
  })

  it('warns when the provider is the default sign-in method, and deleting a disabled one is plain', async () => {
    const wrapper = await mountList([oidc, saml, off], 'OIDC')
    await wrapper.find('button[aria-label="Delete Corporate SSO"]').trigger('click')
    await flushPromises()
    expect(confirmDelete.mock.calls.at(-1)[0].warning).toContain('default sign-in method')

    await wrapper.find('button[aria-label="Delete Old SAML"]').trigger('click')
    await flushPromises()
    expect(typeof confirmDelete.mock.calls.at(-1)[0]).toBe('string')
  })

  it('does not delete when the warning is declined', async () => {
    const wrapper = await mountList([oidc, off])
    confirmDelete.mockResolvedValueOnce(false)
    await wrapper.find('button[aria-label="Delete Corporate SSO"]').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)
  })

  it('the edit page says OpenID Connect changes need a restart, and SAML ones do not', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(json([])))
    const wrapper = mount(IdentityProviderEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    expect(wrapper.find('.hint').text()).toContain('OpenID Connect changes take effect after the application is restarted')

    await wrapper.find('select').setValue('SAML')
    expect(wrapper.text()).not.toContain('take effect after the application is restarted')
  })
})
