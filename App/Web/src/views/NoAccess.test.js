import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import NoAccess from './NoAccess.vue'
import { useRightsStore } from '../stores/rights'

const stub = { template: '<div />' }

async function mountPage() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: stub },
      { path: '/profile', name: 'my-profile', component: stub },
      { path: '/no-access', name: 'no-access', component: stub },
      { path: '/login', name: 'login', component: stub }
    ]
  })
  router.push('/no-access')
  await router.isReady()
  const wrapper = mount(NoAccess, { global: { plugins: [router] } })
  return { wrapper, router }
}

function reloadAnswers(permissionNames) {
  globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ roleNames: [], permissionNames }) }))
}

describe('NoAccess landing page', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    Object.assign(useRightsStore(), { loaded: true, authenticated: true, displayName: 'Pat Doe', permissionNames: [] })
  })
  afterEach(() => vi.restoreAllMocks())

  it('says who is signed in and what to do', async () => {
    const { wrapper } = await mountPage()
    expect(wrapper.find('h1').text()).toBe("You don't have access to BlueTrack yet")
    expect(wrapper.text()).toContain('You are signed in as Pat Doe')
    expect(wrapper.findAll('li')).toHaveLength(2)
    expect(wrapper.find('a').attributes('href')).toBe('/profile')
  })

  it('Check again goes to the Dashboard once a role has been given', async () => {
    reloadAnswers(['ViewAccounts'])
    const { wrapper, router } = await mountPage()
    await wrapper.findAll('button').find(b => b.text() === 'Check again').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('dashboard')
  })

  it('Check again says so when there is still no role', async () => {
    reloadAnswers([])
    const { wrapper, router } = await mountPage()
    await wrapper.findAll('button').find(b => b.text() === 'Check again').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="status"]').text()).toBe('Your account still has no roles.')
    expect(router.currentRoute.value.name).toBe('no-access')
  })
})
