import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia } from 'pinia'
import Login from './Login.vue'

// D-173: after the router guard sends an unauthenticated user here, Windows
// Integrated sign-in has to re-ask /api/me (the rights store cached the 401)
// and continue to returnUrl -- previously the page just sat on /login.
// D-205: the default comes from `defaultProviderType`; /login/<method>
// overrides it.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const windows = { providerType: 'WindowsIntegrated', displayName: 'Windows Integrated', displayOrder: 1 }
const saml = { providerType: 'SAML', displayName: 'Authentik (SAML)', displayOrder: 3 }

function mockFetch({ meStatus = 200, providers = [windows], defaultProviderType = 'WindowsIntegrated' } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/auth/providers') return Promise.resolve(jsonResponse({ providers, defaultProviderType }))
    if (url === '/api/auth/session') {
      return Promise.resolve(jsonResponse({ authenticated: false, windows: defaultProviderType === 'WindowsIntegrated', defaultProviderType }))
    }
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
      { path: '/login/:provider(windows|oidc|saml)', name: 'login-provider', component: Login },
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

// jsdom can't navigate: record where the page sends the browser instead.
let navigatedTo
const realLocation = window.location

describe('Login.vue', () => {
  beforeEach(() => {
    navigatedTo = null
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...realLocation, set href(value) { navigatedTo = value }, get href() { return realLocation.href } }
    })
  })

  afterEach(() => {
    Object.defineProperty(window, 'location', { configurable: true, value: realLocation })
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
    expect(wrapper.text()).toContain('(default)')
  })

  it('Continue retries the Windows sign-in and goes to the dashboard by default', async () => {
    mockFetch({ meStatus: 200 })
    const { wrapper, router } = await mountAt('/login')

    await wrapper.find('button').trigger('click')
    await flushPromises()

    expect(meCalls()).toBe(1)
    expect(router.currentRoute.value.fullPath).toBe('/')
  })

  it('with no default: lists the enabled methods and goes nowhere on its own', async () => {
    mockFetch({ providers: [windows, saml], defaultProviderType: null })
    const { wrapper } = await mountAt('/login?returnUrl=/targets')

    expect(navigatedTo).toBeNull()
    expect(meCalls()).toBe(0)
    expect(wrapper.text()).toContain('Choose a sign-in method')
    expect(wrapper.text()).toContain('Authentik (SAML)')
    expect(wrapper.text()).not.toContain('signs in automatically')
  })

  it('with SAML as the default: goes straight to the SAML sign-in, keeping returnUrl', async () => {
    mockFetch({ providers: [windows, saml], defaultProviderType: 'SAML' })
    await mountAt('/login?returnUrl=/targets')

    expect(navigatedTo).toBe('/api/auth/saml/login?returnUrl=%2Ftargets')
  })

  it('/login/saml overrides a Windows default', async () => {
    mockFetch({ providers: [windows, saml], defaultProviderType: 'WindowsIntegrated' })
    await mountAt('/login/saml')

    expect(navigatedTo).toBe('/api/auth/saml/login?returnUrl=%2F')
    expect(meCalls()).toBe(0)
  })

  it('/login/windows when Windows is not the default records the choice through the API', async () => {
    mockFetch({ providers: [windows, saml], defaultProviderType: 'SAML' })
    await mountAt('/login/windows')

    expect(navigatedTo).toBe('/api/auth/login/windows?returnUrl=%2F')
  })

  it('/login/oidc when OIDC is not enabled says so and lists the methods', async () => {
    mockFetch({ providers: [windows, saml], defaultProviderType: null })
    const { wrapper } = await mountAt('/login/oidc')

    expect(navigatedTo).toBeNull()
    expect(wrapper.find('[role="alert"]').text()).toContain("isn't enabled")
    expect(wrapper.text()).toContain('Authentik (SAML)')
  })
})
