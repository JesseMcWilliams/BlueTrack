import { describe, it, expect, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia } from 'pinia'
import Login from './Login.vue'

// D-173: after the router guard sends an unauthenticated user here, Windows
// Integrated sign-in has to re-ask /api/me (the rights store cached the 401)
// and continue to returnUrl -- previously the page just sat on /login.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const windowsProviders = {
  providers: [{ providerType: 'WindowsIntegrated', displayName: 'Windows Integrated', displayOrder: 1 }]
}

function mockFetch({ meStatus }) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/auth/providers') return Promise.resolve(jsonResponse(windowsProviders))
    if (url === '/api/me') {
      return Promise.resolve(meStatus === 200
        ? jsonResponse({ userKey: 1, displayName: 'Test User', roleNames: [], permissionNames: [], preferences: {} })
        : jsonResponse(null, { ok: false, status: meStatus }))
    }
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

async function mountAt(path) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'dashboard', component: { template: '<div />' } },
      { path: '/login', name: 'login', component: Login },
      { path: '/targets', name: 'targets', component: { template: '<div />' } }
    ]
  })
  await router.push(path)
  const wrapper = mount(Login, { global: { plugins: [router, createPinia()] } })
  await flushPromises()
  return { wrapper, router }
}

function meCalls() {
  return globalThis.fetch.mock.calls.filter(([url]) => url === '/api/me').length
}

describe('Login.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('sent by the router guard: re-checks /api/me and continues to returnUrl once Windows sign-in succeeds', async () => {
    mockFetch({ meStatus: 200 })
    const { router } = await mountAt('/login?returnUrl=/targets')

    expect(meCalls()).toBe(1)
    expect(router.currentRoute.value.fullPath).toBe('/targets')
  })

  it('sent by the router guard: stays on /login with a message when /api/me still returns 401', async () => {
    mockFetch({ meStatus: 401 })
    const { wrapper, router } = await mountAt('/login?returnUrl=/targets')

    expect(router.currentRoute.value.name).toBe('login')
    expect(wrapper.find('[role="alert"]').text()).toContain("Windows sign-in didn't complete")
  })

  it('direct visit (no returnUrl): lists providers without calling /api/me', async () => {
    mockFetch({ meStatus: 200 })
    const { wrapper, router } = await mountAt('/login')

    expect(meCalls()).toBe(0)
    expect(router.currentRoute.value.name).toBe('login')
    expect(wrapper.text()).toContain('Windows Integrated (signs in automatically)')
  })

  it('Continue retries the Windows sign-in and goes to the dashboard by default', async () => {
    mockFetch({ meStatus: 200 })
    const { wrapper, router } = await mountAt('/login')

    await wrapper.find('button').trigger('click')
    await flushPromises()

    expect(meCalls()).toBe(1)
    expect(router.currentRoute.value.fullPath).toBe('/')
  })
})
