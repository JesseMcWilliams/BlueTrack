import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import MyProfile from './MyProfile.vue'
import { useRightsStore } from '../stores/rights'
import { usePageSizeStore } from '../stores/pageSize'

// UI review W12-2 (Reload My Rights stays on the page and says what happened), W12-3 (a preference says whether the
// server kept it) and W12-4 (page size can be set here).
function json(body, ok = true, status = 200) { return { ok, status, json: () => Promise.resolve(body) } }

function mountPage(fetchImpl) {
  setActivePinia(createPinia())
  useRightsStore().$patch({ loaded: true, authenticated: true, displayName: 'Pat Doe', roleNames: ['Viewer'], permissionNames: ['ViewDashboard'] })
  globalThis.fetch = vi.fn(fetchImpl)
  return mount(MyProfile)
}

describe('MyProfile', () => {
  beforeEach(() => {
    // Node's own experimental localStorage shadows the DOM's here; use an in-memory one.
    const store = new Map()
    vi.stubGlobal('localStorage', {
      getItem: key => (store.has(key) ? store.get(key) : null),
      setItem: (key, value) => store.set(key, String(value)),
      removeItem: key => store.delete(key),
      clear: () => store.clear()
    })
  })
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('Reload My Rights keeps the page and says it reloaded', async () => {
    const wrapper = mountPage(() => Promise.resolve(json({ roleNames: ['Viewer', 'Analyst'], permissionNames: ['ViewDashboard'] })))
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Reload My Rights').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Rights reloaded.')
    expect(wrapper.find('dl').text()).toContain('Viewer, Analyst')
    expect(wrapper.find('h2').exists()).toBe(true)
  })

  it('a failed reload shows the error beside the button, not instead of the page', async () => {
    const wrapper = mountPage(() => Promise.resolve(json(null, false, 500)))
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Reload My Rights').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Request failed: 500')
    expect(wrapper.find('dl').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('Rights reloaded.')
  })

  it('says Saved when the server keeps a preference and says it did not when it refuses', async () => {
    let ok = true
    const wrapper = mountPage(() => Promise.resolve(json(null, ok, ok ? 204 : 500)))
    await flushPromises()

    await wrapper.find('input[name="theme"][value="Dark"]').trigger('change')
    await flushPromises()
    expect(wrapper.find('[role="status"]').text()).toBe('Saved.')

    ok = false
    await wrapper.find('input[name="theme"][value="Light"]').trigger('change')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('the server did not keep it')
  })

  it('sets the page size from the profile', async () => {
    const wrapper = mountPage(() => Promise.resolve(json(null, true, 204)))
    await flushPromises()

    await wrapper.find('select').setValue('100')
    await flushPromises()

    expect(usePageSizeStore().current).toBe(100)
    const put = globalThis.fetch.mock.calls.find(([url]) => url === '/api/me/preferences/PageSize')
    expect(JSON.parse(put[1].body)).toEqual({ value: '100' })
    expect(wrapper.find('[role="status"]').text()).toBe('Saved.')
  })
})
