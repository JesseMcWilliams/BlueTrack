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
      { path: '/admin/data-sources', name: 'admin-data-sources', component: { template: '<div />' } },
      { path: '/admin/credentials', name: 'admin-credentials', component: { template: '<div />' } },
      { path: '/admin/data-sources/cyberark', name: 'admin-cyberark-sources-edit', component: { template: '<div />' } }
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
  evdDatabaseName: null,
  selfHostedSource: 'Evd',
  pvwaBaseUrl: null,
  pvwaCredentialKey: null,
  pvwaLogonMethod: 'CyberArk',
  directPullTime: '01:30',
  directMaxChangePercent: 20
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

  // D-248: Self-Hosted Direct.
  const credentials = [
    { credentialKey: 3, credentialName: 'PVWA service user', backendType: 'CyberArkCP' },
    { credentialKey: 4, credentialName: 'Other', backendType: 'WindowsDpapi' }
  ]

  it('shows the EVD field for the EVD source and the PVWA fields for Direct, loading credentials only for Direct', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: { ...defaults, importSelfHosted: true, evdDatabaseName: 'CyberArkSH' } }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials)
    })
    const wrapper = await mountPage()

    expect(wrapper.text()).toContain('EVD database:')
    expect(wrapper.text()).not.toContain('PVWA address')
    expect(globalThis.fetch.mock.calls.some(([url]) => url.endsWith('/credentials'))).toBe(false)

    await wrapper.find('select').setValue('Direct')
    await flushPromises()

    expect(wrapper.text()).toContain('PVWA address')
    expect(wrapper.text()).not.toContain('EVD database:')
    expect(wrapper.text()).toContain('Pending (discovered) accounts are not read')
    expect(wrapper.text()).toContain('PVWA service user (CyberArkCP)')
    expect(wrapper.text()).toContain('Privilege Cloud Direct')
  })

  it('opens straight onto the Direct fields when Direct is saved, and loads the credentials', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({
        isConfigured: true,
        settings: { ...defaults, importSelfHosted: true, selfHostedSource: 'Direct', pvwaBaseUrl: 'https://pvwa.example.com', pvwaCredentialKey: 3 }
      }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials)
    })
    const wrapper = await mountPage()

    expect(wrapper.find('input[placeholder="https://pvwa.example.com"]').element.value).toBe('https://pvwa.example.com')
    expect(globalThis.fetch.mock.calls.some(([url]) => url.endsWith('/credentials'))).toBe(true)
  })

  it('saves the Direct settings as typed', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({
        isConfigured: true,
        settings: { ...defaults, importSelfHosted: true, selfHostedSource: 'Direct', pvwaBaseUrl: 'https://pvwa.example.com', pvwaCredentialKey: 3 }
      }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials),
      'PUT /api/admin/cyberark-sources': jsonResponse(null, true, 204)
    })
    const router = makeRouter()
    await router.push('/')
    const wrapper = await mountPage(router)

    await wrapper.find('input[maxlength="5"]').setValue('01:15')
    await wrapper.find('input[type="number"]').setValue('35')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, options]) => options?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toMatchObject({
      importSelfHosted: true, selfHostedSource: 'Direct', pvwaBaseUrl: 'https://pvwa.example.com', pvwaCredentialKey: 3,
      pvwaLogonMethod: 'CyberArk', directPullTime: '01:15', directMaxChangePercent: 35
    })
    expect(router.currentRoute.value.name).toBe('admin-data-sources')
  })

  it('says so when the credentials could not be loaded', async () => {
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: { ...defaults, importSelfHosted: true, selfHostedSource: 'Direct' } }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(null, false, 403)
    })
    const wrapper = await mountPage()

    expect(wrapper.find('[role="alert"]').text()).toContain('403')
  })

  // D-248: linking to and from Credentials.
  it('links to Admin > Credentials to create a credential, keeping what was typed', async () => {
    sessionStorage.clear()
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: { ...defaults, importSelfHosted: true, selfHostedSource: 'Direct', pvwaBaseUrl: 'https://pvwa.example.com' } }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials)
    })
    const router = makeRouter()
    await router.push('/')
    const wrapper = await mountPage(router)
    await wrapper.find('input[placeholder="https://pvwa.example.com"]').setValue('https://typed.example.com')

    const link = wrapper.findAll('a').find(a => a.text() === 'Create a credential')
    expect(link.attributes('href')).toContain('/admin/credentials?new=CyberArkCP&returnTo=cyberark-sources')
    await link.trigger('click')

    expect(JSON.parse(sessionStorage.getItem('cyberArkSourcesDraft')).editing.pvwaBaseUrl).toBe('https://typed.example.com')
  })

  it('arrives from a credential with Direct and that credential chosen, and restores the draft', async () => {
    sessionStorage.setItem('cyberArkSourcesDraft', JSON.stringify({
      at: Date.now(),
      editing: { ...defaults, importSelfHosted: true, selfHostedSource: 'Evd', evdDatabaseName: 'CyberArkSH', pvwaBaseUrl: 'https://typed.example.com', directMaxChangePercent: 35 }
    }))
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: defaults }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials)
    })
    const router = makeRouter()
    await router.push('/admin/data-sources/cyberark?credentialKey=3')
    const wrapper = await mountPage(router)

    expect(wrapper.find('input[placeholder="https://pvwa.example.com"]').element.value).toBe('https://typed.example.com')
    expect(wrapper.find('input[type="number"]').element.value).toBe('35')
    expect(wrapper.findAll('select')[1].element.value).toBe('3')
    expect(sessionStorage.getItem('cyberArkSourcesDraft')).toBeNull()   // used once
    expect(wrapper.findAll('select')[0].element.value).toBe('Direct')
  })

  it('arrives from a credential without a draft: Direct on, that credential chosen, nothing else changed', async () => {
    sessionStorage.clear()
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: { ...defaults, importPrivilegeCloud: true, exportFolderPath: 'D:\\Exports' } }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials)
    })
    const router = makeRouter()
    await router.push('/admin/data-sources/cyberark?credentialKey=4')
    const wrapper = await mountPage(router)

    expect(wrapper.findAll('select').find(s => s.element.value === '4')).toBeTruthy()
    expect(wrapper.text()).toContain('Export folder')   // Privilege Cloud stays as it was
  })

  it('ignores a draft older than an hour', async () => {
    sessionStorage.setItem('cyberArkSourcesDraft', JSON.stringify({ at: Date.now() - 2 * 3600_000, editing: { ...defaults, pvwaBaseUrl: 'https://stale.example.com' } }))
    routeFetch({
      'GET /api/admin/cyberark-sources': jsonResponse({ isConfigured: true, settings: defaults }),
      'GET /api/admin/cyberark-sources/credentials': jsonResponse(credentials)
    })
    const router = makeRouter()
    await router.push('/admin/data-sources/cyberark?credentialKey=3')
    const wrapper = await mountPage(router)

    expect(wrapper.find('input[placeholder="https://pvwa.example.com"]').element.value).toBe('')
  })
})
