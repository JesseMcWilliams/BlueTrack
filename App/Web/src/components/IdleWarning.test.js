import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import IdleWarning from './IdleWarning.vue'

describe('IdleWarning', () => {
  it('says how long is left, with the plural right, and Stay signed in tells the page', async () => {
    const one = mount(IdleWarning, { props: { warning: { minutesLeft: 1 } } })
    expect(one.text()).toContain('signed out in about 1 minute because')
    const two = mount(IdleWarning, { props: { warning: { minutesLeft: 2 } } })
    expect(two.text()).toContain('signed out in about 2 minutes because')

    await two.find('button').trigger('click')
    expect(two.emitted('stay')).toHaveLength(1)
  })

  it('renders nothing without a warning', () => {
    expect(mount(IdleWarning, { props: { warning: null } }).find('p').exists()).toBe(false)
  })
})
