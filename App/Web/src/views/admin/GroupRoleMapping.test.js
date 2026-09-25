import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import GroupRoleMapping from './GroupRoleMapping.vue'

// D-107/Archive_Planning_Outstanding-Work-Survey-2026-09-16.md: this project had no existing Vitest
// coverage for GroupRoleMapping.vue -- mirrors RiskScoreBands.test.js's own
// globalThis.fetch mocking style, established for a page with no prior
// test file rather than an existing convention to extend.
function jsonResponse(body, ok = true, status = 200) {
  return {
    ok,
    status,
    headers: { get: () => null },
    json: () => Promise.resolve(body)
  }
}

function blobResponse(text, { ok = true, status = 200, fileName = 'Grant-db_backupstatus_reader-BUILTIN_Users.sql' } = {}) {
  return {
    ok,
    status,
    headers: { get: (name) => (name === 'Content-Disposition' ? `attachment; filename="${fileName}"` : null) },
    blob: () => Promise.resolve(new Blob([text], { type: 'text/plain' })),
    json: () => Promise.resolve(null)
  }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/group-role-mapping', name: 'admin-group-role-mapping', component: { template: '<div />' } },
      { path: '/admin/group-role-mapping/new', name: 'admin-group-role-mapping-create', component: { template: '<div />' } }
    ]
  })
}

describe('GroupRoleMapping.vue', () => {
  beforeEach(() => {
    globalThis.fetch = vi.fn()
    // jsdom implements neither of these -- stub so the download flow doesn't throw.
    globalThis.URL.createObjectURL = vi.fn(() => 'blob:mock')
    globalThis.URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('does not show the script-generator button before a lookup succeeds', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))

    const wrapper = mount(GroupRoleMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.findAll('button').some(b => b.text() === 'Generate db_backupstatus_reader Script')).toBe(false)
  })

  it('shows the script-generator button once a group is resolved', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))
    const wrapper = mount(GroupRoleMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({
      resolvedAccountName: 'BUILTIN\\Users',
      sid: 'S-1-5-32-545',
      currentRoleNames: [],
      currentPermissionNames: []
    }))
    await wrapper.get('input').setValue('BUILTIN\\Users')
    await wrapper.get('form').trigger('submit.prevent')
    await flushPromises()

    expect(wrapper.findAll('button').some(b => b.text() === 'Generate db_backupstatus_reader Script')).toBe(true)
  })

  it('clicking the script-generator button downloads the generated script', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))
    const wrapper = mount(GroupRoleMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({
      resolvedAccountName: 'BUILTIN\\Users',
      sid: 'S-1-5-32-545',
      currentRoleNames: [],
      currentPermissionNames: []
    }))
    await wrapper.get('input').setValue('BUILTIN\\Users')
    await wrapper.get('form').trigger('submit.prevent')
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(blobResponse('ALTER ROLE db_backupstatus_reader ADD MEMBER [BUILTIN\\Users];'))
    const generateButton = wrapper.findAll('button').find(b => b.text() === 'Generate db_backupstatus_reader Script')
    await generateButton.trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/group-role-mappings/generate-backupstatus-reader-script', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ groupName: 'BUILTIN\\Users' })
    })
    expect(globalThis.URL.createObjectURL).toHaveBeenCalled()
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalled()
  })

  it('shows an error if the group can no longer be resolved when generating the script', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))
    const wrapper = mount(GroupRoleMapping, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({
      resolvedAccountName: 'BUILTIN\\Users',
      sid: 'S-1-5-32-545',
      currentRoleNames: [],
      currentPermissionNames: []
    }))
    await wrapper.get('input').setValue('BUILTIN\\Users')
    await wrapper.get('form').trigger('submit.prevent')
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, false, 404))
    const generateButton = wrapper.findAll('button').find(b => b.text() === 'Generate db_backupstatus_reader Script')
    await generateButton.trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Could not resolve')
  })
})
