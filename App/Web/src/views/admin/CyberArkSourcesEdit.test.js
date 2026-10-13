import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import CyberArkSourcesEdit from './CyberArkSourcesEdit.vue'

// D-211 (Data Sources phase 3): the CyberArk sources' settings page.

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

const defaults = {
  importPrivilegeCloud: false,
  exportFolderPath: null,
  platformsFileName: 'Export_PlatformsList.csv',
  usersFileName: 'Export_UsersList.csv',
  groupsFileName: 'Export_GroupsList.csv',
  groupMembersFileName: 'Export Local Group Members {yyyy-MM-dd}.csv',
  safesFileName: 'Export_SafesList.csv',
  accountsFileName: 'Export_AccountsList.csv',
  entitlementsFileName: 'Export Entitlements {yyyy-MM-dd}.csv',
  importSelfHosted: false,
  evdDatabaseName: null
}

function routeFetch(handlers) {
  globalThis.fetch = vi.fn((url, options) => {
    const key = `${options?.method ?? 'GET'} ${url}`
    const handler = handlers[key]
    if (!handler) throw new Error(`Unexpected fetch: ${key}`)
    return Promise.resolve(typeof handler === 'function' ? handler() : handler)
  })
}

async function mountPage(router = makeRouter()) {
  const wrapper = mount(CyberArkSourcesEdit, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

describe('CyberArkSourcesEdit.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('says when the sources are not set up, and shows a source\'s fields only when it is on', async () => {
    routeFetch({ 'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: false, settings: defaults }) })
    const wrapper = await mountPage()

    expect(wrapper.text()).toContain('Not set up yet')
    expect(wrapper.text()).not.toContain('Export folder')

    await wrapper.findAll('input[type="checkbox"]')[0].setValue(true)
    expect(wrapper.text()).toContain('Export folder')
    expect(wrapper.text()).toContain('Entitlements file')
  })

  it('saves the settings and returns to Data Sources', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: { ...defaults, importSelfHosted: true, evdDatabaseName: 'CyberArkSH' } }),
      'PUT /api/admin/cyberark-sources': jsonResponse(null, true, 204)
    })
    const router = makeRouter()
    await router.push('/')
    const wrapper = await mountPage(router)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const putCall = globalThis.fetch.mock.calls.find(([, options]) => options?.method === 'PUT')
    expect(JSON.parse(putCall[1].body)).toMatchObject({ importSelfHosted: true, evdDatabaseName: 'CyberArkSH' })
    expect(router.currentRoute.value.name).toBe('admin-data-sources')
  })

  it('shows the API\'s reason when a save is refused', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: defaults }),
      'PUT /api/admin/cyberark-sources': jsonResponse({ detail: 'Turn on at least one source.' }, false, 400)
    })
    const wrapper = await mountPage()

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Turn on at least one source.')
  })

  it('lists each Test check with its outcome', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: { ...defaults, importSelfHosted: true, evdDatabaseName: 'NoSuchDb' } }),
      'POST /api/admin/cyberark-sources/test': jsonResponse([
        { item: 'EVD database', ok: false, message: 'No database named NoSuchDb on BlueTrack\'s SQL Server.' }
      ])
    })
    const wrapper = await mountPage()

    await wrapper.findAll('button').find(b => b.text() === 'Test').trigger('click')
    await flushPromises()

    expect(wrapper.find('.check-failed').text()).toContain('Problem: EVD database: No database named NoSuchDb')
  })
})
