import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import SafeDetail from './SafeDetail.vue'
import { useRightsStore } from '../stores/rights'

// D-308: a Safe's page names its BlueTrack application and lists its first accounts, with a link to all of them.
const stub = { template: '<div />' }
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: stub },
      { path: '/accounts', name: 'account-progress-list', component: stub },
      { path: '/accounts/:accountKey', name: 'account-progress-detail', component: stub },
      { path: '/admin/application-mapping', name: 'admin-application-mapping', component: stub }
    ]
  })
}

function response(body, { status = 200, filteredCount = null } = {}) {
  return {
    ok: status < 400,
    status,
    headers: { get: name => (name === 'X-Filtered-Count' && filteredCount !== null ? String(filteredCount) : null) },
    json: () => Promise.resolve(body)
  }
}

const SAFE = {
  safeKey: 7, safeName: 'LIN_Web', description: 'Web servers', sourceSystemName: 'Self-Hosted', managingCPM: 'PasswordManager',
  createdDate: null, applicationKey: 3, applicationCode: 'WEB', applicationName: 'Web Portal', applicationOwnerName: 'Pat Owner',
  isDecomFlagged: false, isIgnored: false, isInternal: false, activeAccountCount: 60, deletedAccountCount: 2
}
const ACCOUNT = { accountKey: 11, accountName: 'svc-web', userName: 'svc-web', address: 'web01', stageName: 'Discovery', statusName: 'In Progress', ownerName: 'Jane' }

function mockApi({ safe = SAFE, safeStatus = 200, accounts = [ACCOUNT], filteredCount = 1 } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/safes/7') return Promise.resolve(response(safe, { status: safeStatus }))
    if (url.startsWith('/api/account-progress?')) return Promise.resolve(response(accounts, { filteredCount }))
    return Promise.resolve(response(null, { status: 404 }))
  })
}

async function mountPage(permissionNames = ['ViewDashboard']) {
  useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames })
  const wrapper = mount(SafeDetail, { props: { safeKey: '7' }, global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

describe('SafeDetail.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('names the Safe and its BlueTrack application, and asks for its accounts by Safe', async () => {
    mockApi()
    const wrapper = await mountPage()

    expect(wrapper.find('h1').text()).toBe('LIN_Web')
    expect(wrapper.text()).toContain('Web Portal (WEB)')
    expect(wrapper.text()).toContain('Owner: Pat Owner')
    expect(wrapper.text()).toContain('60 accounts, and 2 deleted.')
    const listUrl = globalThis.fetch.mock.calls.map(c => c[0]).find(u => u.startsWith('/api/account-progress?'))
    expect(listUrl).toContain('safe=7')
    expect(wrapper.find('tbody a').attributes('href')).toBe('/accounts/11')
    expect(wrapper.findAll('a').map(a => a.attributes('href'))).toContain('/accounts?safe=7')
  })

  it('says how many it shows when the Safe has more accounts than the page lists', async () => {
    mockApi({ filteredCount: 60 })
    const wrapper = await mountPage()

    expect(wrapper.text()).toContain('Showing the first 1 of 60.')
  })

  it('says no application is mapped, offering the mapping page only to those who may map', async () => {
    mockApi({ safe: { ...SAFE, applicationKey: null, applicationCode: null, applicationName: null, applicationOwnerName: null } })
    let wrapper = await mountPage()
    expect(wrapper.text()).toContain('No BlueTrack application is mapped to this Safe.')
    expect(wrapper.text()).not.toContain('Map it on Application Mapping')

    wrapper = await mountPage(['ViewDashboard', 'CurateApplicationMapping'])
    expect(wrapper.text()).toContain('Map it on Application Mapping')
  })

  it('says when there is no such Safe', async () => {
    mockApi({ safeStatus: 404 })
    const wrapper = await mountPage()

    expect(wrapper.text()).toContain('There is no Safe with this key.')
  })
})
