import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import ApiKeyReveal from './ApiKeyReveal.vue'

vi.mock('../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../composables/useConfirmDialog'

// UI review W10-4: leaving the key's page before copying it asks first.
const stub = { template: '<div />' }
const mounted = []
async function mountReveal() {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', name: 'keys', component: stub }, { path: '/elsewhere', name: 'elsewhere', component: stub }] })
  router.push('/')
  await router.isReady()
  const wrapper = mount(ApiKeyReveal, { props: { apiKey: 'bt_KEY', expiresAt: '2027-01-01T00:00:00Z', heading: 'Created' }, global: { plugins: [router] } })
  mounted.push(wrapper)
  return { wrapper, router }
}

describe('ApiKeyReveal leave guard', () => {
  beforeEach(() => confirmDelete.mockReset())
  afterEach(() => {
    mounted.splice(0).forEach(w => w.unmount()) // each mount adds a window listener
    vi.restoreAllMocks()
  })

  it('asks before leaving with the key not copied, and stays when declined', async () => {
    confirmDelete.mockResolvedValue(false)
    const { router } = await mountReveal()

    await router.push('/elsewhere')

    expect(confirmDelete).toHaveBeenCalledTimes(1)
    expect(confirmDelete.mock.calls[0][0].title).toBe('Leave without copying the key?')
    expect(confirmDelete.mock.calls[0][1]).toBe('Leave')
    expect(router.currentRoute.value.name).toBe('keys')
  })

  it('leaves when confirmed', async () => {
    confirmDelete.mockResolvedValue(true)
    const { router } = await mountReveal()
    await router.push('/elsewhere')
    expect(router.currentRoute.value.name).toBe('elsewhere')
  })

  it('does not ask once the key is copied, or after Done', async () => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: vi.fn().mockResolvedValue() }, configurable: true })
    const copiedCase = await mountReveal()
    await copiedCase.wrapper.findAll('button').find(b => b.text() === 'Copy').trigger('click')
    await Promise.resolve()
    await copiedCase.router.push('/elsewhere')
    expect(confirmDelete).not.toHaveBeenCalled()
    copiedCase.wrapper.unmount()

    const doneCase = await mountReveal()
    await doneCase.wrapper.findAll('button').find(b => b.text() === 'Done').trigger('click')
    await doneCase.router.push('/elsewhere')
    expect(confirmDelete).not.toHaveBeenCalled()
  })

  it('asks the browser before the tab is closed with the key not copied', async () => {
    const { wrapper } = await mountReveal()
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount()
    const after = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(after)
    expect(after.defaultPrevented).toBe(false)
  })
})
