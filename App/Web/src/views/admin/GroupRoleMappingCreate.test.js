import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import GroupRoleMappingCreate from './GroupRoleMappingCreate.vue'

// D-204: the Add Mapping page's provider choice.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const roles = [{ appRoleKey: 1, roleName: 'Admin' }, { appRoleKey: 3, roleName: 'Analyst' }]
const providers = [
  { providerKey: 1, providerType: 'WindowsIntegrated', displayName: 'Windows Integrated', isEnabled: true, groupClaimType: null },
  { providerKey: 4, providerType: 'SAML', displayName: 'Authentik (SAML)', isEnabled: true, groupClaimType: 'http://schemas.xmlsoap.org/claims/Group' }
]

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/group-role-mapping', name: 'admin-group-role-mapping', component: { template: '<div />' } }
    ]
  })
}

async function mountPage() {
  globalThis.fetch.mockResolvedValueOnce(jsonResponse(roles))
  globalThis.fetch.mockResolvedValueOnce(jsonResponse(providers))
  const wrapper = mount(GroupRoleMappingCreate, { global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

describe('GroupRoleMappingCreate.vue', () => {
  beforeEach(() => {
    globalThis.fetch = vi.fn()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('defaults to the Windows provider and asks for a DOMAIN\\Group name', async () => {
    const wrapper = await mountPage()

    expect(wrapper.findAll('select')[0].element.value).toBe('1')
    expect(wrapper.text()).toContain('DOMAIN\\GroupName')
  })

  it('for a SAML provider, names the groups claim and posts the provider with the group as typed', async () => {
    const wrapper = await mountPage()

    await wrapper.findAll('option').find(o => o.text().includes('(SAML)')).setSelected()
    expect(wrapper.text()).toContain('exactly as the identity provider sends it')
    expect(wrapper.text()).toContain('http://schemas.xmlsoap.org/claims/Group')

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({ mappingKey: 9 }, true, 201))
    await wrapper.get('input').setValue('BlueTrack-Analyst')
    await wrapper.findAll('select')[1].setValue('Analyst')
    await wrapper.get('form').trigger('submit.prevent')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenLastCalledWith('/api/admin/group-role-mappings', expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({ groupName: 'BlueTrack-Analyst', roleName: 'Analyst', providerKey: 4 })
    }))
  })

  it('says so when the mapping already exists', async () => {
    const wrapper = await mountPage()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, false, 409))
    await wrapper.get('input').setValue('BUILTIN\\Users')
    await wrapper.get('form').trigger('submit.prevent')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('already mapped')
  })
})
