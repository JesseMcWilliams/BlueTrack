import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import EmptyListMessage from './EmptyListMessage.vue'

describe('EmptyListMessage', () => {
  it('shows the message when asked to', () => {
    const wrapper = mount(EmptyListMessage, { props: { show: true, message: 'No targets yet.' } })
    expect(wrapper.text()).toBe('No targets yet.')
  })

  it('renders nothing otherwise', () => {
    const wrapper = mount(EmptyListMessage, { props: { show: false, message: 'No targets yet.' } })
    expect(wrapper.text()).toBe('')
  })
})
