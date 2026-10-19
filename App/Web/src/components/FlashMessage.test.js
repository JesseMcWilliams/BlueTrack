import { describe, it, expect, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import FlashMessage from './FlashMessage.vue'
import { flash, flashDismiss } from '../composables/useFlash'

// UI review theme 3: the one-time "saved" line.
describe('FlashMessage.vue', () => {
  beforeEach(() => {
    flashDismiss()
  })

  it('shows nothing when there is no message', () => {
    expect(mount(FlashMessage).find('.flash-message').exists()).toBe(false)
  })

  it('shows the message as a status, with a Dismiss button', async () => {
    const wrapper = mount(FlashMessage)
    flash('Target saved.')
    await wrapper.vm.$nextTick()

    const line = wrapper.get('[role="status"]')
    expect(line.text()).toContain('Target saved.')
    expect(wrapper.get('button').attributes('aria-label')).toBe('Dismiss')
  })

  it('Dismiss removes it', async () => {
    const wrapper = mount(FlashMessage)
    flash('Target saved.')
    await wrapper.vm.$nextTick()

    await wrapper.get('button').trigger('click')

    expect(wrapper.find('.flash-message').exists()).toBe(false)
  })
})
