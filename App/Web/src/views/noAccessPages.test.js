import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccessDenied from './AccessDenied.vue'
import NotFound from './NotFound.vue'

const stub = { template: '<div />' }
const router = createRouter({
  history: createMemoryHistory(),
  routes: [{ path: '/', name: 'dashboard', component: stub }, { path: '/profile', name: 'my-profile', component: stub }]
})

// UI review W1-2: a page you may not open and an address that matches no page are different messages, each with a way on.
describe('access denied and not found pages', () => {
  it('Access Denied names the problem and links home and to My Profile', () => {
    const wrapper = mount(AccessDenied, { global: { plugins: [router] } })
    expect(wrapper.find('h1').text()).toBe('Access Denied')
    expect(wrapper.text()).not.toContain("doesn't exist")
    expect(wrapper.findAll('a').map(a => a.attributes('href'))).toEqual(['/', '/profile'])
  })

  it('Not Found says the address matches no page and links home', () => {
    const wrapper = mount(NotFound, { global: { plugins: [router] } })
    expect(wrapper.find('h1').text()).toBe('Page Not Found')
    expect(wrapper.text()).not.toContain('permission')
    expect(wrapper.findAll('a').map(a => a.attributes('href'))).toEqual(['/'])
  })
})
