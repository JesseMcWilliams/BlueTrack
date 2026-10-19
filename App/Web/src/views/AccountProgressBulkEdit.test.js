import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccountProgressBulkEdit from './AccountProgressBulkEdit.vue'
import { useAccountSelectionStore } from '../stores/accountSelection'

vi.mock('../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../composables/useConfirmDialog'

// UI review W2-6: Apply overwrites fields on every selected account, so it names the fields and the count first.
const result = { updated: 2, skipped: [] }

async function mountPage() {
  setActivePinia(createPinia())
  const selection = useAccountSelectionStore()
  selection.add([11, 12])
  globalThis.fetch = vi.fn((url, init) => Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve(init?.method === 'POST' ? result : {})
  }))
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', name: 'account-progress-list', component: { template: '<div />' } }] })
  router.push('/')
  await router.isReady()
  const wrapper = mount(AccountProgressBulkEdit, { global: { plugins: [router] } })
  await flushPromises()
  await wrapper.find('input[aria-label="Change Owner"]').setValue(true)
  await wrapper.find('input[aria-label="Owner"]').setValue('Pat Doe')
  return wrapper
}

describe('Account bulk edit', () => {
  beforeEach(() => confirmDelete.mockReset())
  afterEach(() => vi.restoreAllMocks())

  it('asks first, naming the count and the fields, then applies', async () => {
    confirmDelete.mockResolvedValue(true)
    const wrapper = await mountPage()

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const [message, label] = confirmDelete.mock.calls[0]
    expect(message.title).toBe('Change these fields on 2 accounts?')
    expect(message.details).toEqual(['Owner'])
    expect(label).toBe('Apply')
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/account-progress/bulk-edit' && init?.method === 'POST')).toBe(true)
  })

  // UI review W2-9
  it('says so when the dropdown choices could not be loaded', async () => {
    const wrapper = await mountPage()
    expect(wrapper.text()).not.toContain('could not be loaded')

    setActivePinia(createPinia())
    useAccountSelectionStore().add([11])
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve(null) }))
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', name: 'account-progress-list', component: { template: '<div />' } }] })
    router.push('/')
    await router.isReady()
    const failing = mount(AccountProgressBulkEdit, { global: { plugins: [router] } })
    await flushPromises()
    expect(failing.find('[role="alert"]').text()).toContain('could not be loaded (500)')
  })

  it('changes nothing when the confirmation is cancelled', async () => {
    confirmDelete.mockResolvedValue(false)
    const wrapper = await mountPage()

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
  })
})
