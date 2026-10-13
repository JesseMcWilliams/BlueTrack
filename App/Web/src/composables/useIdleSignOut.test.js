import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { useIdleSignOut, LAST_ACTIVITY_KEY } from './useIdleSignOut'
import { useRightsStore } from '../stores/rights'

// D-209: the SPA signs out after Idle Timeout minutes without activity in any tab.
let clock
let api

const Host = defineComponent({
  setup() {
    api = useIdleSignOut({ now: () => clock, checkEveryMs: 1000 })
    return () => h('div')
  }
})

async function mountHost({ minutes = 30, authenticated = true } = {}) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: { template: '<div />' } },
      { path: '/login', name: 'login', component: { template: '<div />' } }
    ]
  })
  await router.push('/')
  useRightsStore().$patch({ authenticated, loaded: true, idleTimeoutMinutes: minutes })
  const wrapper = mount(Host, { global: { plugins: [router] } })
  return { wrapper, router }
}

describe('useIdleSignOut', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    clock = 1_000_000
    // Node's own experimental localStorage shadows jsdom's here; use an in-memory one.
    const store = new Map()
    vi.stubGlobal('localStorage', {
      getItem: key => (store.has(key) ? store.get(key) : null),
      setItem: (key, value) => store.set(key, String(value)),
      removeItem: key => store.delete(key),
      clear: () => store.clear()
    })
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: true, status: 204 }))
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('signs out after the idle timeout with no activity, with the idle reason', async () => {
    const { router, wrapper } = await mountHost({ minutes: 30 })

    clock += 30 * 60000 + 1
    await api.check()
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/auth/logout', { method: 'POST' })
    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query).toEqual({ signedOut: '1', reason: 'idle' })
    expect(useRightsStore().authenticated).toBeNull()
    wrapper.unmount()
  })

  it('stays signed in while there is activity', async () => {
    const { router, wrapper } = await mountHost({ minutes: 30 })

    clock += 20 * 60000
    window.dispatchEvent(new Event('keydown'))
    clock += 20 * 60000
    await api.check()
    await flushPromises()

    expect(globalThis.fetch).not.toHaveBeenCalled()
    expect(router.currentRoute.value.name).toBe('dashboard')
    wrapper.unmount()
  })

  it('counts activity in another tab (shared through localStorage)', async () => {
    const { wrapper } = await mountHost({ minutes: 30 })

    clock += 25 * 60000
    localStorage.setItem(LAST_ACTIVITY_KEY, String(clock)) // another tab was used just now
    clock += 10 * 60000
    await api.check()

    expect(globalThis.fetch).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('does nothing when nobody is signed in', async () => {
    const { wrapper } = await mountHost({ minutes: 30, authenticated: false })

    clock += 60 * 60000
    await api.check()

    expect(globalThis.fetch).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
