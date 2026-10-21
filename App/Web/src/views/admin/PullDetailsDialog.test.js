import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import PullDetailsDialog from './PullDetailsDialog.vue'

// D-248: the detail of one Self-Hosted Direct pull.

const pull = (overrides = {}) => ({
  pullKey: 5, startedAt: '2026-10-19T07:30:00', completedAt: '2026-10-19T07:30:09', status: 'Succeeded', triggeredBy: 'schedule',
  acceptedChange: false, safeCount: 40, accountCount: 2452, userCount: 43, groupCount: 21, memberCount: 88, platformCount: 20, message: null,
  ...overrides
})

describe('PullDetailsDialog.vue', () => {
  it('shows the outcome, who started it, how long it took and what it read', () => {
    const wrapper = mount(PullDetailsDialog, { props: { pull: pull() } })

    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('Succeeded')
    expect(wrapper.text()).toContain('by schedule')
    expect(wrapper.text()).toContain('It took 9 seconds.')
    expect(wrapper.text()).toContain('No messages.')
    const rows = wrapper.findAll('.pull-details-counts tr').map(r => r.text())
    expect(rows).toContain('Safes40')
    expect(rows).toContain('Accounts2452')
    expect(rows).toContain('Safe members88')
    expect(rows).toContain('Users43')
    expect(rows).toContain('Groups21')
    expect(rows).toContain('Platforms20')
    expect(wrapper.text()).not.toContain('Accept the change and pull')   // advice only for a rejected or failed pull
  })

  it('lists each message separately, as warnings for a pull that succeeded', () => {
    const wrapper = mount(PullDetailsDialog, { props: { pull: pull({ message: 'PVWA reported 44 users but returned 43 | User \'a\': details not read' }) } })

    expect(wrapper.findAll('.pull-details-messages li').map(li => li.text())).toEqual([
      'PVWA reported 44 users but returned 43',
      "User 'a': details not read"
    ])
    expect(wrapper.text()).toContain('Any messages below are warnings')
  })

  it('explains a rejected pull and what to do', () => {
    const wrapper = mount(PullDetailsDialog, { props: { pull: pull({ status: 'Rejected', acceptedChange: false, message: 'Safes went from 40 to 10' }) } })

    expect(wrapper.text()).toContain('wasn\'t used')
    expect(wrapper.text()).toContain('use Accept the change and pull')
    expect(wrapper.text()).toContain('List Accounts and View Safe Members')
  })

  it('explains a failed pull, which has no counts', () => {
    const wrapper = mount(PullDetailsDialog, {
      props: { pull: pull({ status: 'Failed', safeCount: null, accountCount: null, userCount: null, groupCount: null, memberCount: null, platformCount: null, completedAt: null, message: 'CyberArk CP request failed (Other): APPPR502E' }) }
    })

    expect(wrapper.text()).toContain("didn't finish")
    expect(wrapper.text()).toContain('APPPR502E')
    expect(wrapper.find('.pull-details-counts').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('It took')
    expect(wrapper.text()).toContain('test it on Admin > Credentials')
  })

  it('notes a change an admin accepted', () => {
    const wrapper = mount(PullDetailsDialog, { props: { pull: pull({ acceptedChange: true, triggeredBy: 'adm.jesse' }) } })

    expect(wrapper.text()).toContain('An admin accepted a change over the allowed percentage.')
  })

  it('closes from the Close button, Escape and the backdrop', async () => {
    const wrapper = mount(PullDetailsDialog, { props: { pull: pull() } })

    await wrapper.find('button').trigger('click')
    await wrapper.find('[role="dialog"]').trigger('keydown', { key: 'Escape' })
    await wrapper.find('.pull-details-backdrop').trigger('click')

    expect(wrapper.emitted('close')).toHaveLength(3)
  })

  // D-250
  it('shows what the last good pull had next to a Safe or account count that changed', () => {
    const wrapper = mount(PullDetailsDialog, { props: { pull: pull({ status: 'Rejected', safeCount: 10, accountCount: 100, baselineSafeCount: 40, baselineAccountCount: 2452 }) } })

    const rows = wrapper.findAll('.pull-details-counts tr').map(r => r.text())
    expect(rows).toContain('Safes10 (was 40)')
    expect(rows).toContain('Accounts100 (was 2452)')
    expect(rows).toContain('Users43')   // not compared
  })

  it('leaves out "was" when the count did not change or there is no earlier pull', () => {
    const same = mount(PullDetailsDialog, { props: { pull: pull({ baselineSafeCount: 40, baselineAccountCount: 2452 }) } })
    const none = mount(PullDetailsDialog, { props: { pull: pull({ baselineSafeCount: null, baselineAccountCount: null }) } })

    expect(same.text()).not.toContain('(was')
    expect(none.text()).not.toContain('(was')
  })
})
