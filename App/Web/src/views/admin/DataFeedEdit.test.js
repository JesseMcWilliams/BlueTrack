import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import DataFeedEdit from './DataFeedEdit.vue'

// D-212 (Data Sources phase 4a): an account inventory feed names the source
// system its accounts come from, and a new source system can be added here.

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/data-sources', name: 'admin-data-sources', component: { template: '<div />' } }
    ]
  })
}

const options = {
  sourceSystems: [{ sourceSystemKey: 11, sourceSystemCode: 'CMDB', sourceSystemName: 'CMDB' }],
  feedTypes: [
    { value: 'SafeAssignments', label: 'Safe → application assignments', usesMappingProfile: false, usesSourceSystem: false, mappingProfiles: [] },
    { value: 'AccountInventory', label: 'Account inventory', usesMappingProfile: false, usesSourceSystem: true, mappingProfiles: [] }
  ]
}

function routeFetch(handlers) {
  globalThis.fetch = vi.fn((url, init) => {
    const key = `${init?.method ?? 'GET'} ${url}`
    const handler = handlers[key]
    if (!handler) throw new Error(`Unexpected fetch: ${key}`)
    return Promise.resolve(typeof handler === 'function' ? handler(init) : handler)
  })
}

async function mountNew() {
  const router = makeRouter()
  await router.push('/')
  const wrapper = mount(DataFeedEdit, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

describe('DataFeedEdit.vue source system (D-212)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the source system only for an account inventory feed, and clears it when the type changes', async () => {
    routeFetch({ 'GET /api/admin/data-feeds/options': jsonResponse(options) })
    const { wrapper } = await mountNew()
    const importSelect = wrapper.findAll('select')[0]

    await importSelect.setValue('SafeAssignments')
    expect(wrapper.text()).not.toContain('Source system:')

    await importSelect.setValue('AccountInventory')
    expect(wrapper.text()).toContain('Source system:')
    expect(wrapper.text()).toContain('UserName and Address (required)')
    await wrapper.findAll('select')[1].setValue(11)

    await importSelect.setValue('SafeAssignments')
    await importSelect.trigger('change')
    expect(wrapper.text()).not.toContain('Source system:')
  })

  it('adds a new source system, selects it, and saves the feed with it', async () => {
    routeFetch({
      'GET /api/admin/data-feeds/options': jsonResponse(options),
      'POST /api/admin/data-feeds/source-systems': jsonResponse({ sourceSystemKey: 12, sourceSystemCode: 'LINUX', sourceSystemName: 'Linux servers' }, true, 201),
      'POST /api/admin/data-feeds': jsonResponse({ dataFeedKey: 5 }, true, 201)
    })
    const { wrapper, router } = await mountNew()
    await wrapper.findAll('select')[0].setValue('AccountInventory')

    await wrapper.findAll('button').find(b => b.text() === '+ New source system').trigger('click')
    await wrapper.find('input[placeholder="Linux servers (CMDB)"]').setValue('Linux servers')
    await wrapper.find('input[placeholder="LINUX_CMDB"]').setValue('LINUX')
    await wrapper.findAll('button').find(b => b.text() === 'Add source system').trigger('click')
    await flushPromises()

    const addCall = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/data-feeds/source-systems')
    expect(JSON.parse(addCall[1].body)).toEqual({ sourceSystemCode: 'LINUX', sourceSystemName: 'Linux servers' })
    expect(wrapper.findAll('select')[1].element.value).toBe('12')

    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const saveCall = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/data-feeds' && init?.method === 'POST')
    expect(JSON.parse(saveCall[1].body)).toMatchObject({ feedType: 'AccountInventory', sourceSystemKey: 12 })
    expect(router.currentRoute.value.name).toBe('admin-data-sources')
  })

  it('shows why a new source system was refused', async () => {
    routeFetch({
      'GET /api/admin/data-feeds/options': jsonResponse(options),
      'POST /api/admin/data-feeds/source-systems': jsonResponse({ detail: "A source system with code 'CMDB' or name 'CMDB' already exists." }, false, 409)
    })
    const { wrapper } = await mountNew()
    await wrapper.findAll('select')[0].setValue('AccountInventory')
    await wrapper.findAll('button').find(b => b.text() === '+ New source system').trigger('click')
    await wrapper.findAll('button').find(b => b.text() === 'Add source system').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('already exists')
  })
})
