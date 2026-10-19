import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia } from 'pinia'
import AdminHub from './AdminHub.vue'
import { useRightsStore } from '../../stores/rights'

// D-250: a warning marker on Data Sources while the latest Self-Hosted Direct pull is failed or rejected.

function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: page },
      { path: '/admin/data-sources', name: 'admin-data-sources', component: page },
      { path: '/admin/credentials', name: 'admin-credentials', component: page }
    ]
  })
}

async function mountHub(permissions, status) {
  globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(status) }))
  const router = makeRouter()
  await router.push('/')
  const pinia = createPinia()
  const rights = useRightsStore(pinia)
  rights.permissionNames = permissions
  rights.loaded = true
  const wrapper = mount(AdminHub, { global: { plugins: [router, pinia], stubs: { 'router-view': true } } })
  await flushPromises()
  return wrapper
}

describe('AdminHub.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it.each(['Failed', 'Rejected'])('marks Data Sources when the latest pull is %s', async (latestStatus) => {
    const wrapper = await mountHub(['ManageDataSources'], { enabled: true, latestStatus })

    expect(wrapper.find('.nav-warning').attributes('aria-label')).toBe('The latest Self-Hosted Direct pull failed or was rejected.')
    expect(wrapper.find('.nav-warning').element.closest('a').getAttribute('href')).toBe('/admin/data-sources')
  })

  it.each([
    [{ enabled: true, latestStatus: 'Succeeded' }],
    [{ enabled: true, latestStatus: null }],
    [{ enabled: false }]
  ])('does not mark it for %j', async (status) => {
    const wrapper = await mountHub(['ManageDataSources'], status)

    expect(wrapper.find('.nav-warning').exists()).toBe(false)
  })

  it('does not ask, or mark anything, for someone without ManageDataSources', async () => {
    const wrapper = await mountHub(['ManageCredentials'], { enabled: true, latestStatus: 'Failed' })

    expect(globalThis.fetch).not.toHaveBeenCalled()
    expect(wrapper.find('.nav-warning').exists()).toBe(false)
  })

  it('shows no marker, and no error, when the status request fails', async () => {
    globalThis.fetch = vi.fn(() => Promise.reject(new Error('network')))
    const router = makeRouter()
    await router.push('/')
    const pinia = createPinia()
    const rights = useRightsStore(pinia)
    rights.permissionNames = ['ManageDataSources']
    rights.loaded = true
    const wrapper = mount(AdminHub, { global: { plugins: [router, pinia], stubs: { 'router-view': true } } })
    await flushPromises()

    expect(wrapper.find('.nav-warning').exists()).toBe(false)
    expect(wrapper.text()).toContain('Data Sources')
  })
})
