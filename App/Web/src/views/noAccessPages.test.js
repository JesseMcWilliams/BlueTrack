import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { useRightsStore } from '../stores/rights'
import AccessDenied from './AccessDenied.vue'
import NotFound from './NotFound.vue'

const stub = { template: '<div />' }
const router = createRouter({
  history: createMemoryHistory(),
  routes: [{ path: '/', name: 'home', component: stub }, { path: '/profile', name: 'my-profile', component: stub }]
})

// UI review W1-2: a page you may not open and an address that matches no page are different messages, each with a way on.
describe('access denied and not found pages', () => {
  it('Access Denied names the problem and links home and to My Profile', () => {
    setActivePinia(createPinia())
    useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames: ['ViewDashboard'] })
    const wrapper = mount(AccessDenied, { global: { plugins: [router] } })
    expect(wrapper.find('h1').text()).toBe('Access Denied')
    expect(wrapper.text()).not.toContain("doesn't exist")
    expect(wrapper.findAll('a').map(a => a.attributes('href'))).toEqual(['/', '/profile'])
  })

  // D-307: Home, which everyone with a role may open, is offered whatever the user's permissions.
  it('Access Denied links home for a user without ViewDashboard too', () => {
    setActivePinia(createPinia())
    useRightsStore().$patch({ loaded: true, authenticated: true, permissionNames: ['ViewAuditLog'] })
    const wrapper = mount(AccessDenied, { global: { plugins: [router] } })
    expect(wrapper.findAll('a').map(a => a.attributes('href'))).toEqual(['/', '/profile'])
  })

  it('Not Found says the address matches no page and links home', () => {
    const wrapper = mount(NotFound, { global: { plugins: [router] } })
    expect(wrapper.find('h1').text()).toBe('Page Not Found')
    expect(wrapper.text()).not.toContain('permission')
    expect(wrapper.findAll('a').map(a => a.attributes('href'))).toEqual(['/'])
  })
})
