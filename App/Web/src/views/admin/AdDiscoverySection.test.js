import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import AdDiscoverySection from './AdDiscoverySection.vue'

// D-317: AD Account Discovery on Data Sources: the domains it reads, its run time, Run now and recent runs.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: { template: '<div />' } }, { path: '/admin/credentials', name: 'admin-credentials', component: { template: '<div />' } }]
  })
}

const STATUS = {
  runTime: '04:30', enabledDomainCount: 2, isRunning: false,
  recentRuns: [{ adDiscoveryRunKey: 1, triggeredBy: 'Schedule', startedAt: '2026-11-04T04:30:00', finishedAt: '2026-11-04T04:31:00', status: 'Partial', domainCount: 2, domainsRead: 1, accountsFound: 40, message: 'corp.example: couldn\'t connect.' }]
}

function routeFetch(handlers) {
  globalThis.fetch = vi.fn((url, options) => {
    const handler = handlers[`${options?.method ?? 'GET'} ${url}`]
    if (!handler) throw new Error(`Unexpected fetch: ${options?.method ?? 'GET'} ${url}`)
    return Promise.resolve(typeof handler === 'function' ? handler(options) : handler)
  })
}

async function mountSection() {
  const wrapper = mount(AdDiscoverySection, { global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

describe('AdDiscoverySection.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the domains, the run time and the recent runs', async () => {
    routeFetch({ 'GET /api/admin/ad-discovery': jsonResponse(STATUS) })
    const wrapper = await mountSection()

    expect(wrapper.text()).toContain('Reads 2 enabled LDAP domains.')
    expect(wrapper.find('input[type="time"]').element.value).toBe('04:30')
    expect(wrapper.find('tbody').text()).toContain('Partial')
    expect(wrapper.find('tbody').text()).toContain('40 accounts from 1 of 2 domains')
    expect(wrapper.find('tbody').text()).toContain('couldn\'t connect')
  })

  it('saves the run time', async () => {
    routeFetch({
      'GET /api/admin/ad-discovery': jsonResponse(STATUS),
      'PUT /api/admin/ad-discovery/settings': jsonResponse(null, true, 204)
    })
    const wrapper = await mountSection()

    await wrapper.find('input[type="time"]').setValue('05:15')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toEqual({ runTime: '05:15' })
    expect(wrapper.text()).toContain('Saved.')
  })

  it('runs now, and says why when it is already running', async () => {
    routeFetch({
      'GET /api/admin/ad-discovery': jsonResponse(STATUS),
      'POST /api/admin/ad-discovery/run': jsonResponse({ detail: 'AD Account Discovery is already running.' }, false, 409)
    })
    const wrapper = await mountSection()

    await wrapper.findAll('button').find(b => b.text() === 'Run now').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('AD Account Discovery is already running.')
  })
})
