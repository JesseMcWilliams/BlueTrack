import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import Targets from './Targets.vue'
import AccessGroups from './AccessGroups.vue'
import RolesAndPermissions from './admin/RolesAndPermissions.vue'
import IdentityProviders from './admin/IdentityProviders.vue'

// UI review theme 3: an empty list says why it is empty. The filterable lists tell "nothing exists yet" from "nothing
// matches the filters" by the X-Total-Count header; the admin lists have only the first case.
function answer(total) {
  return {
    ok: true,
    status: 200,
    headers: { get: (name) => (name === 'X-Total-Count' ? String(total) : name === 'X-Filtered-Count' ? '0' : null) },
    json: () => Promise.resolve([])
  }
}

async function mountPage(component, total) {
  globalThis.fetch = vi.fn(() => Promise.resolve(answer(total)))
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div />' } }, { path: '/:pathMatch(.*)*', component: { template: '<div />' } }] })
  router.push('/')
  await router.isReady()
  const wrapper = mount(component, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

describe('list empty states', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it.each([
    ['Targets', Targets, 'No targets yet.', 'No targets match the current filters.'],
    ['Access Groups', AccessGroups, 'No access groups yet.', 'No access groups match the current filters.']
  ])('%s: says nothing exists yet when the total is zero', async (_name, component, none) => {
    const wrapper = await mountPage(component, 0)
    expect(wrapper.find('.empty-list').text()).toContain(none)
  })

  it.each([
    ['Targets', Targets, 'No targets match the current filters.'],
    ['Access Groups', AccessGroups, 'No access groups match the current filters.']
  ])('%s: says nothing matches when rows exist but the filters hide them all', async (_name, component, filtered) => {
    const wrapper = await mountPage(component, 12)
    expect(wrapper.find('.empty-list').text()).toBe(filtered)
  })

  it.each([
    ['Roles', RolesAndPermissions, 'No roles are defined yet.'],
    ['Identity providers', IdentityProviders, 'No identity providers are configured yet.']
  ])('%s: says none are defined', async (_name, component, none) => {
    const wrapper = await mountPage(component, 0)
    expect(wrapper.find('.empty-list').text()).toBe(none)
  })
})
