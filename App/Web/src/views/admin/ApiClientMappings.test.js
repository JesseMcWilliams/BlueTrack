import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ApiClientMappings from './ApiClientMappings.vue'

// D-243 (phase 2): the API client mappings section of Admin > API Keys.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const options = {
  providers: [{ providerKey: 3, displayName: 'authentik' }],
  roles: [{ appRoleKey: 4, roleName: 'Viewer' }, { appRoleKey: 3, roleName: 'Auditor' }],
  matchTypes: ['ClientId', 'Group']
}

const mappings = [
  { apiClientMappingKey: 1, providerKey: 3, providerName: 'authentik', matchType: 'ClientId', matchValue: 'svc-reports', appRoleKey: 3, roleName: 'Auditor', displayName: 'Report script', displayOrder: 50, isEnabled: true, createdAt: '2026-10-17T10:00:00', createdByName: 'Admin', lastUsedAt: '2026-10-17T12:30:15' },
  { apiClientMappingKey: 2, providerKey: 3, providerName: 'authentik', matchType: 'Group', matchValue: 'bt-readers', appRoleKey: 4, roleName: 'Viewer', displayName: null, displayOrder: 100, isEnabled: false, createdAt: '2026-10-17T10:00:00', createdByName: 'Admin', lastUsedAt: null }
]

function mockApi(list = mappings, opts = options, overrides = {}) {
  globalThis.fetch = vi.fn((url, init = {}) => {
    const key = `${init.method ?? 'GET'} ${url}`
    if (overrides[key]) return Promise.resolve(overrides[key])
    if (key === 'GET /api/admin/api-client-mappings') return Promise.resolve(jsonResponse(list))
    if (key === 'GET /api/admin/api-client-mappings/options') return Promise.resolve(jsonResponse(opts))
    return Promise.resolve(jsonResponse(null, true, 204))
  })
}

describe('ApiClientMappings.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists each mapping with its provider, match, role and status', async () => {
    mockApi()
    const wrapper = mount(ApiClientMappings)
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0].text()).toContain('authentik')
    expect(rows[0].text()).toContain('Client')
    expect(rows[0].text()).toContain('svc-reports')
    expect(rows[0].text()).toContain('Auditor')
    expect(rows[0].text()).toContain('Enabled')
    expect(rows[1].text()).toContain('Group')
    expect(rows[1].text()).toContain('Disabled')
    expect(rows[1].text()).toContain('Never')
  })

  it('says so when there are no mappings, and when no OIDC provider is enabled', async () => {
    mockApi([], { providers: [], roles: [], matchTypes: [] })
    const wrapper = mount(ApiClientMappings)
    await flushPromises()

    expect(wrapper.text()).toContain('There are no API client mappings yet.')
    expect(wrapper.text()).toContain('Mappings need an enabled OIDC identity provider.')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('adds a mapping with the chosen provider, match, role and order', async () => {
    mockApi([])
    const wrapper = mount(ApiClientMappings)
    await flushPromises()

    const selects = wrapper.findAll('select')
    await selects[1].setValue('Group')
    await wrapper.get('input[maxlength="300"]').setValue('bt-auditors')
    await wrapper.findAll('option').find(o => o.text() === 'Auditor').setSelected()
    await wrapper.get('input[type="number"]').setValue('10')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post[0]).toBe('/api/admin/api-client-mappings')
    expect(JSON.parse(post[1].body)).toEqual({ providerKey: 3, matchType: 'Group', matchValue: 'bt-auditors', appRoleKey: 3, displayName: null, displayOrder: 10 })
  })

  it('shows why a mapping was refused', async () => {
    mockApi([], options, { 'POST /api/admin/api-client-mappings': jsonResponse({ error: 'An API caller can’t hold the Admin role.' }, false, 400) })
    const wrapper = mount(ApiClientMappings)
    await flushPromises()
    await wrapper.get('input[maxlength="300"]').setValue('x')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('Admin role')
  })

  it('Disable, Enable and Delete (after a confirmation) call the mapping', async () => {
    mockApi()
    const wrapper = mount(ApiClientMappings)
    await flushPromises()
    const rows = () => wrapper.findAll('tbody tr')

    await rows()[0].findAll('button').find(b => b.text() === 'Disable').trigger('click')
    await flushPromises()
    await rows()[1].findAll('button').find(b => b.text() === 'Enable').trigger('click')
    await flushPromises()
    confirmDelete.mockResolvedValueOnce(false)
    await rows()[0].findAll('button').find(b => b.text() === 'Delete').trigger('click')
    await flushPromises()
    await rows()[0].findAll('button').find(b => b.text() === 'Delete').trigger('click')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.filter(([, init]) => init?.method).map(([url, init]) => `${init.method} ${url}`)
    expect(calls).toEqual([
      'POST /api/admin/api-client-mappings/1/disable',
      'POST /api/admin/api-client-mappings/2/enable',
      'DELETE /api/admin/api-client-mappings/1'
    ])
    expect(confirmDelete).toHaveBeenLastCalledWith(expect.stringContaining('Client svc-reports'), 'Delete')
  })
})
