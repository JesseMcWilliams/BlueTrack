import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia } from 'pinia'
import Credentials from './Credentials.vue'
import { useRightsStore } from '../../stores/rights'

// D-248: the links between Credentials and the CyberArk sources page, and the blank-folder hint.

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/credentials', name: 'admin-credentials', component: { template: '<div />' } },
      { path: '/admin/data-sources/cyberark', name: 'admin-cyberark-sources-edit', component: { template: '<div />' } }
    ]
  })
}

const credential = { credentialKey: 5, credentialName: 'PVWA_REST', backendType: 'CyberArkCP', username: null, vaultSafe: 'S', vaultFolder: 'Root', vaultObject: 'O' }

function routeFetch(extra = {}) {
  const handlers = {
    'GET /api/admin/credentials': jsonResponse([credential]),
    'GET /api/admin/credentials/ldap-config': jsonResponse([]),
    ...extra
  }
  globalThis.fetch = vi.fn((url, options) => {
    const key = `${options?.method ?? 'GET'} ${url}`
    const handler = handlers[key]
    if (!handler) throw new Error(`Unexpected fetch: ${key}`)
    return Promise.resolve(handler)
  })
}

async function mountAt(path, permissions = ['ManageDataSources']) {
  const router = makeRouter()
  await router.push(path)
  const pinia = createPinia()
  const wrapper = mount(Credentials, { global: { plugins: [router, pinia] } })
  useRightsStore(pinia).permissionNames = permissions
  await flushPromises()
  return { wrapper, router }
}

describe('Credentials.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('links each credential to the CyberArk sources page, with the credential chosen', async () => {
    routeFetch()
    const { wrapper } = await mountAt('/admin/credentials')

    const link = wrapper.findAll('a').find(a => a.text() === 'Use for Self-Hosted Direct')
    expect(link.attributes('href')).toBe('/admin/data-sources/cyberark?credentialKey=5')
  })

  it('opens the new-credential form for a CyberArk CP credential when asked to by the sources page', async () => {
    routeFetch()
    const { wrapper } = await mountAt('/admin/credentials?new=CyberArkCP&returnTo=cyberark-sources')

    expect(wrapper.find('form').exists()).toBe(true)
    expect(wrapper.find('select').element.value).toBe('CyberArkCP')
    expect(wrapper.text()).toContain('you go back to the CyberArk sources page')
    expect(wrapper.find('input[placeholder="Root"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('leave blank for Root')
  })

  it('goes back to the sources page with the new credential after it is saved', async () => {
    routeFetch({ 'POST /api/admin/credentials': { ok: true, status: 201, json: () => Promise.resolve({ credentialKey: 9 }) } })
    const { wrapper, router } = await mountAt('/admin/credentials?new=CyberArkCP&returnTo=cyberark-sources')

    const inputs = wrapper.findAll('form input')
    await inputs[0].setValue('Direct PVWA')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, options]) => options?.method === 'POST')
    expect(JSON.parse(post[1].body)).toMatchObject({ credentialName: 'Direct PVWA', backendType: 'CyberArkCP' })
    expect(router.currentRoute.value.fullPath).toBe('/admin/data-sources/cyberark?credentialKey=9')
  })

  it('stays on the page after a save when it was not opened from the sources page', async () => {
    routeFetch({ 'POST /api/admin/credentials': { ok: true, status: 201, json: () => Promise.resolve({ credentialKey: 9 }) } })
    const { wrapper, router } = await mountAt('/admin/credentials?new=CyberArkCP')

    await wrapper.findAll('form input')[0].setValue('Plain')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('admin-credentials')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('only hints at Root for the CyberArk vault types', async () => {
    routeFetch()
    const { wrapper } = await mountAt('/admin/credentials?new=CyberArkConjur')

    expect(wrapper.find('select').element.value).toBe('CyberArkConjur')
    expect(wrapper.text()).not.toContain('leave blank for Root')
  })

  // D-250
  it('hides the link to the sources page from someone who cannot open it', async () => {
    routeFetch()
    const { wrapper } = await mountAt('/admin/credentials', ['ManageCredentials'])

    expect(wrapper.findAll('a').some(a => a.text() === 'Use for Self-Hosted Direct')).toBe(false)
  })

  it('shows what uses each credential, and will not delete one that is in use', async () => {
    routeFetch({
      'GET /api/admin/credentials': jsonResponse([
        { ...credential, usedBy: ['CyberArk sources (Self-Hosted Direct)', 'LDAP configuration (company.com)'] },
        { ...credential, credentialKey: 6, credentialName: 'Spare', usedBy: [] }
      ])
    })
    const { wrapper } = await mountAt('/admin/credentials')

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].text()).toContain('CyberArk sources (Self-Hosted Direct); LDAP configuration (company.com)')
    const deleteButtons = rows.map(r => r.findAll('button').find(b => b.text() === 'Delete'))
    expect(deleteButtons[0].attributes('disabled')).toBeDefined()
    expect(deleteButtons[0].attributes('title')).toBe('In use. Change what uses it first.')
    expect(deleteButtons[1].attributes('disabled')).toBeUndefined()
  })

  it('offers to cancel and return to the sources page when it was opened from there', async () => {
    routeFetch()
    const { wrapper } = await mountAt('/admin/credentials?new=CyberArkCP&returnTo=cyberark-sources')

    const link = wrapper.findAll('a').find(a => a.text() === 'Cancel and return to CyberArk sources')
    expect(link.attributes('href')).toBe('/admin/data-sources/cyberark')
  })

  it('has no return link when opened on its own', async () => {
    routeFetch()
    const { wrapper } = await mountAt('/admin/credentials?new=CyberArkCP')

    expect(wrapper.findAll('a').some(a => a.text() === 'Cancel and return to CyberArk sources')).toBe(false)
  })

  it('shows the API\'s reason when a save is refused', async () => {
    routeFetch({ 'POST /api/admin/credentials': { ok: false, status: 400, json: () => Promise.resolve({ detail: 'A credential with that name already exists.' }) } })
    const { wrapper } = await mountAt('/admin/credentials?new=CyberArkCP')

    await wrapper.findAll('form input')[0].setValue('PVWA_REST')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('A credential with that name already exists.')
  })

  it('falls back to the status when the refusal has no reason', async () => {
    routeFetch({ 'POST /api/admin/credentials': { ok: false, status: 500, json: () => Promise.reject(new Error('not json')) } })
    const { wrapper } = await mountAt('/admin/credentials?new=CyberArkCP')

    await wrapper.findAll('form input')[0].setValue('X')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('500')
  })
})
