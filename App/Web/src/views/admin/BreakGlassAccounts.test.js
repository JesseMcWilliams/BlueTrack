import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import BreakGlassAccounts from './BreakGlassAccounts.vue'

// D-216: the break-glass accounts section of Identity Providers.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const account = {
  breakGlassAccountKey: 3,
  externalIdentifier: 'S-1-5-21-1-2-3-500',
  displayName: '.\\Administrator',
  notes: 'Safe BreakGlass',
  createdDate: '2026-10-13T20:00:00',
  createdByName: 'Admin'
}

describe('BreakGlassAccounts.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists the accounts with their identifiers', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([account])))
    const wrapper = mount(BreakGlassAccounts)
    await flushPromises()

    expect(wrapper.text()).toContain('.\\Administrator')
    expect(wrapper.text()).toContain('S-1-5-21-1-2-3-500')
  })

  it('adds an account and shows why one was refused', async () => {
    const responses = [jsonResponse([]), jsonResponse({ detail: "'x' isn't a Windows account this server can find." }, false, 400)]
    globalThis.fetch = vi.fn(() => Promise.resolve(responses.shift()))
    const wrapper = mount(BreakGlassAccounts)
    await flushPromises()

    await wrapper.find('input[placeholder=".\\\\Administrator"]').setValue('x\\y')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(JSON.parse(post[1].body)).toMatchObject({ account: 'x\\y' })
    expect(wrapper.find('[role="alert"]').text()).toContain("isn't a Windows account")
  })

  it('Remove asks first, then deletes and reloads', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'DELETE' ? jsonResponse(null, true, 204) : jsonResponse([account])))
    const wrapper = mount(BreakGlassAccounts)
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Remove').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalled()
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/admin/break-glass-accounts/3' && init?.method === 'DELETE')).toBe(true)
  })
})
