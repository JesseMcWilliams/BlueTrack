import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import Targets from './Targets.vue'
import Combobox from '../components/Combobox.vue'

// UI review W4-3 and W4-7: Link a Single Account to a Target finds both ends by typing and shows the API's reason;
// each row's Delete button names the row.
const account = { accountKey: 41, userName: 'svc_payroll', accountName: 'svc_payroll', address: 'pay01.example.com' }
const target = { targetKey: 7, targetName: 'pay01', targetTypeDisplayName: 'Server', identifiers: [] }
const row = { targetKey: 7, targetTypeKey: 1, targetTypeDisplayName: 'Server', targetName: 'pay01', applicationKey: null, riskScore: 1, identifiers: [] }

function json(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: (n) => (n === 'X-Total-Count' || n === 'X-Filtered-Count' ? '1' : null) }, json: () => (body === undefined ? Promise.reject(new Error('none')) : Promise.resolve(body)) }
}

async function mountPage(post) {
  globalThis.fetch = vi.fn((url, init) => {
    if (init?.method === 'POST') return Promise.resolve(post)
    if (url.startsWith('/api/admin/targets?')) return Promise.resolve(json([row]))
    return Promise.resolve(json([]))
  })
  const stub = { template: '<div />' }
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/targets', name: 'targets', component: stub }, { path: '/t/:targetKey', name: 'target-edit', component: stub }, { path: '/t/new', name: 'target-create', component: stub }, { path: '/b', name: 'targets-bulk-import', component: stub }, { path: '/:p(.*)*', component: stub }]
  })
  router.push('/targets')
  await router.isReady()
  const wrapper = mount(Targets, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

function choose(wrapper, accountItem, targetItem) {
  const boxes = wrapper.findAllComponents(Combobox)
  if (accountItem) boxes[0].vm.$emit('update:modelValue', accountItem)
  if (targetItem) boxes[1].vm.$emit('update:modelValue', targetItem)
}

describe('Targets: link an account to a target, and row labels', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('posts the chosen account name and target key', async () => {
    const wrapper = await mountPage(json({}, { status: 201 }))
    choose(wrapper, account, target)

    await wrapper.findAll('form').find(f => f.text().includes('Link')).trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post[0]).toBe('/api/admin/risk-scoring/account-target-links')
    expect(JSON.parse(post[1].body)).toEqual({ accountName: 'svc_payroll', targetKey: 7 })
    expect(wrapper.findAll('[role="status"]').map(p => p.text())).toContain('Linked.')
  })

  it('needs both an account and a target before it sends anything', async () => {
    const wrapper = await mountPage(json({}))
    choose(wrapper, account, null)

    await wrapper.findAll('form').find(f => f.text().includes('Link')).trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('Choose both an account and a target from the lists.')
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })

  it("shows the API's reason when the link is refused", async () => {
    const wrapper = await mountPage(json({ detail: 'That account is already linked.' }, { ok: false, status: 409 }))
    choose(wrapper, account, target)

    await wrapper.findAll('form').find(f => f.text().includes('Link')).trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('That account is already linked.')
  })

  it("names the row in each Delete button", async () => {
    const wrapper = await mountPage(json({}))
    expect(wrapper.find('tbody button[aria-label="Delete pay01"]').exists()).toBe(true)
  })
})

describe('Targets: a failed load', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('shows the error and no stale rows, and not the empty-list message', async () => {
    globalThis.fetch = vi.fn((url) => Promise.resolve(url.startsWith('/api/admin/targets?') ? json(null, { ok: false, status: 500 }) : json([])))
    const stub = { template: '<div />' }
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/targets', name: 'targets', component: stub }, { path: '/:p(.*)*', component: stub }] })
    router.push('/targets')
    await router.isReady()
    const wrapper = mount(Targets, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('Request failed: 500')
    expect(wrapper.findAll('tbody tr')).toHaveLength(0)
    expect(wrapper.find('.empty-list').exists()).toBe(false)
  })
})
