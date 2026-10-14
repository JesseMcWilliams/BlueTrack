import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import UserMenu from './UserMenu.vue'
import { useRightsStore } from '../stores/rights'
vi.mock('../utils/navigateAway', () => ({ navigateAway: vi.fn() }))
import { navigateAway } from '../utils/navigateAway'

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

  // D-213: About shows the version in a dialog; Escape closes it and focus returns to the menu button.
  it('About shows the version, and Escape closes it', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ version: '1.2.3' }) }))
    const { wrapper } = await mountMenu()
    const button = wrapper.get('button.user-menu__button')
    await button.trigger('click')

    await wrapper.findAll('button').find(b => b.text() === 'About').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/about')
    const dialog = wrapper.get('[role="dialog"]')
    expect(dialog.text()).toContain('Version 1.2.3')
    expect(button.attributes('aria-expanded')).toBe('false')

    await dialog.trigger('keydown', { key: 'Escape' })
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    expect(document.activeElement).toBe(button.element)
    wrapper.unmount()
  })

  // D-217: a SAML session's sign-out continues at the identity provider (Single Logout).
  it('Sign out of a SAML session goes to the Single Logout address the API gives', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ singleLogoutUrl: '/BlueTrack/api/auth/saml/logout' }) }))
    const { wrapper, router, rights } = await mountMenu()
    await wrapper.get('button.user-menu__button').trigger('click')

    await wrapper.findAll('button').find(b => b.text() === 'Sign out').trigger('click')
    await flushPromises()

    expect(navigateAway).toHaveBeenCalledWith('/BlueTrack/api/auth/saml/logout')
    expect(rights.authenticated).toBeFalsy()   // reset before leaving
    expect(router.currentRoute.value.name).toBe('dashboard')   // no SPA navigation: the browser leaves
    wrapper.unmount()
  })
})
