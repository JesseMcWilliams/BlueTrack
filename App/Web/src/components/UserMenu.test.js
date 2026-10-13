import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import UserMenu from './UserMenu.vue'
import { useRightsStore } from '../stores/rights'

// D-206: the top nav's user menu.
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: { template: '<div />' } },
      { path: '/profile', name: 'my-profile', component: { template: '<div />' } },
      { path: '/login', name: 'login', component: { template: '<div />' } }
    ]
  })
}

async function mountMenu(displayName = 'Jesse McWilliams') {
  const router = makeRouter()
  await router.push('/')
  const rights = useRightsStore()
  rights.$patch({ displayName, authenticated: true, loaded: true, permissionNames: ['ViewDashboard'] })
  const wrapper = mount(UserMenu, { global: { plugins: [router] }, attachTo: document.body })
  return { wrapper, router, rights }
}

describe('UserMenu.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 204 }))
  })

  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  it("shows the signed-in user's name, closed until clicked", async () => {
    const { wrapper } = await mountMenu()
    const button = wrapper.get('button.user-menu__button')

    expect(button.text()).toContain('Jesse McWilliams')
    expect(button.attributes('aria-expanded')).toBe('false')
    expect(wrapper.get('#user-menu-list').isVisible()).toBe(false)

    await button.trigger('click')
    expect(button.attributes('aria-expanded')).toBe('true')
    expect(wrapper.get('#user-menu-list').isVisible()).toBe(true)
    expect(wrapper.text()).toContain('Profile & Preferences')
    expect(wrapper.text()).toContain('Sign out')
    wrapper.unmount()
  })

  it('falls back to "Account" without a display name', async () => {
    const { wrapper } = await mountMenu(null)
    expect(wrapper.get('button.user-menu__button').text()).toContain('Account')
    wrapper.unmount()
  })

  it('Escape closes the list and puts focus back on the button', async () => {
    const { wrapper } = await mountMenu()
    const button = wrapper.get('button.user-menu__button')
    await button.trigger('click')

    await wrapper.get('.user-menu').trigger('keydown', { key: 'Escape' })

    expect(button.attributes('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(button.element)
    wrapper.unmount()
  })

  it('a click outside closes the list', async () => {
    const { wrapper } = await mountMenu()
    await wrapper.get('button.user-menu__button').trigger('click')

    document.body.click()
    await flushPromises()

    expect(wrapper.get('button.user-menu__button').attributes('aria-expanded')).toBe('false')
    wrapper.unmount()
  })

  it('Sign out ends the session, clears the rights and goes to the sign-in page', async () => {
    const { wrapper, router, rights } = await mountMenu()
    await wrapper.get('button.user-menu__button').trigger('click')

    await wrapper.findAll('#user-menu-list button').find(b => b.text() === 'Sign out').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/auth/logout', { method: 'POST' })
    expect(rights.authenticated).toBeNull()
    expect(rights.displayName).toBeNull()
    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.signedOut).toBe('1')
    wrapper.unmount()
  })

  it('a failed sign-out says so and stays put', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: false, status: 500 }))
    const { wrapper, router } = await mountMenu()
    await wrapper.get('button.user-menu__button').trigger('click')

    await wrapper.findAll('#user-menu-list button').find(b => b.text() === 'Sign out').trigger('click')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('Sign out failed: 500')
    expect(router.currentRoute.value.name).toBe('dashboard')
    wrapper.unmount()
  })
})
