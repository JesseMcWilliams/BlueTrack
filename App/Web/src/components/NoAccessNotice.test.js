import { describe, it, expect, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import NoAccessNotice from './NoAccessNotice.vue'
import { useRightsStore } from '../stores/rights'

const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/profile', name: 'my-profile', component: { template: '<div />' } }] })

function mountWith(state) {
  setActivePinia(createPinia())
  Object.assign(useRightsStore(), state)
  return mount(NoAccessNotice, { global: { plugins: [router] } })
}

describe('NoAccessNotice', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('tells a signed-in user with no permissions why, and links to the profile', () => {
    const wrapper = mountWith({ loaded: true, authenticated: true, permissionNames: [] })
    expect(wrapper.text()).toContain('Your account has no roles in BlueTrack')
    expect(wrapper.find('a').attributes('href')).toBe('/profile')
  })

  it('stays out of the way for a user with permissions', () => {
    expect(mountWith({ loaded: true, authenticated: true, permissionNames: ['ViewAccounts'] }).find('p').exists()).toBe(false)
  })

  it('says nothing before rights load or when not signed in', () => {
    expect(mountWith({ loaded: false, authenticated: null, permissionNames: [] }).find('p').exists()).toBe(false)
    expect(mountWith({ loaded: true, authenticated: false, permissionNames: [] }).find('p').exists()).toBe(false)
  })
})
