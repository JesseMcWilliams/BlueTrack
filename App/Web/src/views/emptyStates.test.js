import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import Targets from './Targets.vue'
import AccessGroups from './AccessGroups.vue'
import RolesAndPermissions from './admin/RolesAndPermissions.vue'
import IdentityProviders from './admin/IdentityProviders.vue'
import FieldMetadataManagement from './admin/FieldMetadataManagement.vue'

// UI review theme 3: an empty list says why it is empty. The filterable lists tell "nothing exists yet" from "nothing
// matches the filters" by the X-Total-Count header; the admin lists have only the first case.
function answer(total) {
  return {
    ok: true,
    status: 200,
    headers: { get: (name) => (name === 'X-Total-Count' ? String(total) : name === 'X-Filtered-Count' ? '0' : null) },
    json: () => Promise.resolve([])
  }
}

async function mountPage(component, total) {
  globalThis.fetch = vi.fn(() => Promise.resolve(answer(total)))
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }, { path: '/:pathMatch(.*)*', component: { template: '<div />' } }] })
  router.push('/')
  await router.isReady()
  const wrapper = mount(component, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

describe('list empty states', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it.each([
    ['Targets', Targets, 'No targets yet.', 'No targets match the current filters.'],
    ['Access Groups', AccessGroups, 'No access groups yet.', 'No access groups match the current filters.']
  ])('%s: says nothing exists yet when the total is zero', async (_name, component, none) => {
    const wrapper = await mountPage(component, 0)
    expect(wrapper.find('.empty-list').text()).toContain(none)
  })

  it.each([
    ['Targets', Targets, 'No targets match the current filters.'],
    ['Access Groups', AccessGroups, 'No access groups match the current filters.']
  ])('%s: says nothing matches when rows exist but the filters hide them all', async (_name, component, filtered) => {
    const wrapper = await mountPage(component, 12)
    expect(wrapper.find('.empty-list').text()).toBe(filtered)
  })

  it.each([
    ['Roles', RolesAndPermissions, 'No roles are defined yet.'],
    ['Identity providers', IdentityProviders, 'No identity providers are configured yet.']
  ])('%s: says none are defined', async (_name, component, none) => {
    const wrapper = await mountPage(component, 0)
    expect(wrapper.find('.empty-list').text()).toBe(none)
  })
})

// UI review W1-6, W11-6: a yes/no column says Yes or No, not true or false.
describe('yes/no columns', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  async function mountWithRows(component, rows) {
    globalThis.fetch = vi.fn((url) => Promise.resolve({
      ok: true, status: 200, headers: { get: () => null }, json: () => Promise.resolve(url.endsWith('/default') ? null : rows)
    }))
    const stub = { template: '<div />' }
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', component: stub },
        { path: '/idp/:providerKey', name: 'admin-identity-provider-edit', component: stub },
        { path: '/idp/new', name: 'admin-identity-provider-create', component: stub },
        { path: '/fields/:fieldMetadataKey', name: 'admin-field-metadata-edit', component: stub },
        { path: '/fields/new', name: 'admin-field-metadata-create', component: stub }
      ]
    })
    router.push('/')
    await router.isReady()
    const wrapper = mount(component, { global: { plugins: [router] } })
    await flushPromises()
    return wrapper
  }

  it('Identity Providers shows Enabled as Yes or No', async () => {
    const wrapper = await mountWithRows(IdentityProviders, [
      { providerKey: 1, providerType: 'Oidc', displayName: 'Corporate', isEnabled: true },
      { providerKey: 2, providerType: 'Saml', displayName: 'Old', isEnabled: false }
    ])
    const cells = wrapper.find('table').findAll('tbody tr').map(r => r.findAll('td')[2].text())
    expect(cells).toEqual(['Yes', 'No'])
  })

  it('Field Metadata shows Required as Yes or No', async () => {
    const wrapper = await mountWithRows(FieldMetadataManagement, [
      { fieldMetadataKey: 1, fieldName: 'Owner', displayLabel: 'Owner', isRequired: true },
      { fieldMetadataKey: 2, fieldName: 'Notes', displayLabel: 'Notes', isRequired: false }
    ])
    const text = wrapper.findAll('tbody tr').map(r => r.text())
    expect(text[0]).toContain('Yes')
    expect(text[1]).toContain('No')
    expect(wrapper.text()).not.toMatch(/true|false/)
  })
})
