import { describe, it, expect, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { createRouter, createMemoryHistory } from 'vue-router'
import { useAddressState } from './useAddressState'
import { compactQuery } from '../utils/listQuery'

// UI review W7-2: a page's state in its address.
function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', name: 'home', component: page }, { path: '/report', name: 'report', component: page }, { path: '/other', name: 'other', component: page }]
  })
}

function harness({ onApplied } = {}) {
  const state = { name: ref(''), page: ref(1) }
  const applied = []
  const Page = defineComponent({
    setup() {
      useAddressState({
        sources: () => [state.name.value, state.page.value],
        apply(query) {
          applied.push({ ...query })
          state.name.value = typeof query.name === 'string' ? query.name : ''
          state.page.value = Number(query.page) > 0 ? Number(query.page) : 1
        },
        current: () => compactQuery({ name: state.name.value, page: state.page.value }, { page: 1 }),
        onApplied
      })
      return () => h('div')
    }
  })
  return { Page, state, applied }
}

async function mountAt(path, options) {
  const router = makeRouter()
  await router.push(path)
  const h1 = harness(options)
  const wrapper = mount(h1.Page, { global: { plugins: [router] } })
  await flushPromises()
  return { router, wrapper, ...h1 }
}

describe('useAddressState', () => {
  it('applies the address to the page once, at the start', async () => {
    const { state, applied } = await mountAt('/report?name=abc&page=3')

    expect(applied).toEqual([{ name: 'abc', page: '3' }])
    expect(state.name.value).toBe('abc')
    expect(state.page.value).toBe(3)
  })

  it('writes a change to the address, with defaults left out', async () => {
    const { router, state } = await mountAt('/report')

    state.name.value = 'xyz'
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ name: 'xyz' })

    state.page.value = 2
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ name: 'xyz', page: '2' })

    state.name.value = ''
    state.page.value = 1
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({})
  })

  it('replaces the address entry, so Back leaves the page', async () => {
    const router = makeRouter()
    await router.push('/')
    await router.push('/report')
    const { Page, state } = harness()
    mount(Page, { global: { plugins: [router] } })
    await flushPromises()

    state.name.value = 'a'
    await flushPromises()
    state.name.value = 'ab'
    await flushPromises()
    router.back()
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('home')
  })

  it('follows the address when it changes under the page, and then calls onApplied once', async () => {
    const onApplied = vi.fn()
    const { router, state, applied } = await mountAt('/report?name=first', { onApplied })

    await router.push('/report?name=second&page=4')
    await flushPromises()

    expect(state.name.value).toBe('second')
    expect(state.page.value).toBe(4)
    expect(applied).toHaveLength(2)
    expect(onApplied).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.query).toEqual({ name: 'second', page: '4' })   // not rewritten while applying
  })

  it('does nothing when the address already says what the page shows', async () => {
    const onApplied = vi.fn()
    const { router, state, applied } = await mountAt('/report', { onApplied })
    state.name.value = 'a'
    await flushPromises()

    await router.replace({ query: { name: 'a' } })
    await flushPromises()

    expect(applied).toHaveLength(1)
    expect(onApplied).not.toHaveBeenCalled()
  })

  it('leaves the address alone once the route is no longer the page\'s own', async () => {
    const { router, state, applied } = await mountAt('/report?name=a')

    await router.push('/other')
    await flushPromises()
    state.name.value = 'changed after leaving'
    await flushPromises()

    expect(router.currentRoute.value.query).toEqual({})
    expect(applied).toHaveLength(1)
  })

  it('does not touch an address when there is no router', async () => {
    const { Page, state, applied } = harness()
    mount(Page)
    await flushPromises()

    state.name.value = 'a'
    await flushPromises()

    expect(applied).toHaveLength(0)
  })
})
