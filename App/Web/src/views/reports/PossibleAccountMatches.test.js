import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import PossibleAccountMatches from './PossibleAccountMatches.vue'

// D-212 (Data Sources phase 4c): the Possible Account Matches report.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, { ok = true, status = 200, count = null } = {}) {
  return {
    ok,
    status,
    headers: { get: (name) => (name === 'X-Total-Count' || name === 'X-Filtered-Count') && count !== null ? String(count) : null },
    json: () => Promise.resolve(body)
  }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/account-progress/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }
    ]
  })
}

const row = {
  reconciliationKey: 9,
  matchMethod: 'FeedShortName',
  reason: "'web01' and 'web01.corp.example.com' have the same host name, 'web01'.",
  matchedDate: '2026-10-13T04:00:00',
  feedAccountKey: 50,
  feedUserName: 'root',
  feedAddress: 'web01',
  feedSourceName: 'CMDB',
  cyberArkAccountKey: 7,
  cyberArkAccountName: 'Operating System-UnixSSH-web01-root',
  cyberArkUserName: 'root',
  cyberArkAddress: 'web01.corp.example.com',
  cyberArkSourceName: 'CyberArk Privilege Cloud (SaaS)'
}

describe('PossibleAccountMatches.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists each pair with why it was suggested', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([row], { count: 1 })))
    const wrapper = mount(PossibleAccountMatches, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(globalThis.fetch.mock.calls[0][0]).toContain('/api/reports/possible-matches?page=1')
    expect(wrapper.text()).toContain('Operating System-UnixSSH-web01-root')
    expect(wrapper.text()).toContain('Same host name')
    expect(wrapper.text()).toContain('CMDB')
  })

  it('Link asks first, posts, and reloads', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST' ? jsonResponse(null, { status: 204 }) : jsonResponse([row], { count: 1 })))
    const wrapper = mount(PossibleAccountMatches, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Link').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('Operating System-UnixSSH-web01-root'), 'Link')
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/reports/possible-matches/9/link' && init?.method === 'POST')).toBe(true)
    expect(globalThis.fetch.mock.calls.filter(([url]) => url.startsWith('/api/reports/possible-matches?'))).toHaveLength(2)
  })

  it('Dismiss shows the reason when the pair is no longer open', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST'
      ? jsonResponse({ detail: 'It was already linked or dismissed' }, { ok: false, status: 409 })
      : jsonResponse([row], { count: 1 })))
    const wrapper = mount(PossibleAccountMatches, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Dismiss').trigger('click')
    await flushPromises()

    expect(confirmDelete).not.toHaveBeenCalled()
    expect(wrapper.find('[role="alert"]').text()).toContain('already linked or dismissed')
  })
})
