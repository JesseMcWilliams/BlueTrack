import { describe, it, expect, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import ConfirmDialog from './ConfirmDialog.vue'
import { confirmDelete, respondToConfirmDialog } from '../composables/useConfirmDialog'

// D-128: the one shared confirmation dialog every Delete button in the app
// awaits before proceeding (see composables/useConfirmDialog.js for why
// it's a singleton rather than a per-page instance). These tests drive the
// real confirmDelete()/respondToConfirmDialog() pair the app itself uses,
// not a mock -- ConfirmDialog.vue and the composable are tested together.
describe('ConfirmDialog.vue', () => {
  afterEach(() => {
    // The composable's state is a module-level singleton -- leaving it
    // "visible" from a test that didn't respond would leak into the next one.
    respondToConfirmDialog(false)
  })

  it('renders nothing until confirmDelete() is called', () => {
    const wrapper = mount(ConfirmDialog)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('shows the message and resolves true when Confirm is clicked', async () => {
    const wrapper = mount(ConfirmDialog)
    const resultPromise = confirmDelete('Delete Target "web01"? This cannot be undone.')
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('Delete Target "web01"? This cannot be undone.')

    await wrapper.findAll('button').find(b => b.text() === 'Delete').trigger('click')
    expect(await resultPromise).toBe(true)
    await wrapper.vm.$nextTick()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('resolves false when Cancel is clicked', async () => {
    const wrapper = mount(ConfirmDialog)
    const resultPromise = confirmDelete('Delete Target "web01"? This cannot be undone.')
    await wrapper.vm.$nextTick()

    await wrapper.findAll('button').find(b => b.text() === 'Cancel').trigger('click')
    expect(await resultPromise).toBe(false)
  })

  it('resolves false on Escape', async () => {
    const wrapper = mount(ConfirmDialog)
    const resultPromise = confirmDelete('Delete Target "web01"? This cannot be undone.')
    await wrapper.vm.$nextTick()

    await wrapper.find('[role="dialog"]').trigger('keydown', { key: 'Escape' })
    expect(await resultPromise).toBe(false)
  })

  it('uses a custom confirmLabel when provided', async () => {
    const wrapper = mount(ConfirmDialog)
    confirmDelete('Deactivate this recipient?', 'Deactivate')
    await wrapper.vm.$nextTick()

    expect(wrapper.findAll('button').some(b => b.text() === 'Deactivate')).toBe(true)
  })

  // D-131: a structured { title, details, warning } message (D-129's
  // Access Group/Target confirmation) renders details in their own
  // indented block, distinct from the CSS class classes.confirm-dialog-title/
  // .confirm-dialog-warning wrap.
  it('renders a structured message with an indented details block', async () => {
    const wrapper = mount(ConfirmDialog)
    confirmDelete({
      title: 'Delete Target',
      details: ['Name: web01', 'Scope: Server', 'Address: web01.example.com', 'Source: Manual'],
      warning: 'This cannot be undone.'
    })
    await wrapper.vm.$nextTick()

    expect(wrapper.get('.confirm-dialog-title').text()).toBe('Delete Target')
    const details = wrapper.get('.confirm-dialog-details')
    expect(details.findAll('.confirm-dialog-detail-line').map(l => l.text())).toEqual([
      'Name: web01', 'Scope: Server', 'Address: web01.example.com', 'Source: Manual'
    ])
    expect(wrapper.get('.confirm-dialog-warning').text()).toBe('This cannot be undone.')
  })
})
