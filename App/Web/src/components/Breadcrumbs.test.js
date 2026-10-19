import { describe, it, expect, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia } from 'pinia'
import Breadcrumbs from './Breadcrumbs.vue'
import { useListStateStore } from '../stores/listState'

// UI review W2-2: a list crumb leads back to the list as it was left, not to an unfiltered page 1.
function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: page },
      { path: '/accounts', name: 'account-progress-list', meta: { breadcrumb: 'shell.routes.accountProgressList' }, component: page },
      { path: '/accounts/:accountKey', name: 'account-progress-detail', meta: { breadcrumb: 'shell.routes.accountProgressDetail', breadcrumbParent: 'account-progress-list' }, component: page }
    ]
  })
}

describe('Breadcrumbs.vue', () => {
  let pinia
  beforeEach(() => {
    pinia = createPinia()
  })

  async function mountAt(path) {
    const router = makeRouter()
    await router.push(path)
    const wrapper = mount(Breadcrumbs, { global: { plugins: [router, pinia] } })
    await flushPromises()
    return wrapper
  }

  it('links the list crumb with the remembered filters, sort and page', async () => {
    useListStateStore(pinia).remember('account-progress-list', { stage: 'Discovery', page: '2' })
    const wrapper = await mountAt('/accounts/42')

    const links = wrapper.findAll('a')
    expect(links[0].attributes('href')).toBe('/')   // the Dashboard
    expect(links[1].attributes('href')).toBe('/accounts?stage=Discovery&page=2')
    expect(links).toHaveLength(3)
  })

  it('links a crumb with nothing remembered plainly', async () => {
    const wrapper = await mountAt('/accounts/42')

    expect(wrapper.findAll('a')[1].attributes('href')).toBe('/accounts')
  })
})
