import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import Combobox from './Combobox.vue'

// D-225: the reusable APG combobox (Target Match Review's "Merged" target).
const TARGETS = [
  { targetKey: 1, targetName: 'web01' },
  { targetKey: 2, targetName: 'web02' },
  { targetKey: 3, targetName: 'web03' }
]

function mountCombobox(search = vi.fn(() => Promise.resolve(TARGETS)), props = {}) {
  const wrapper = mount(Combobox, {
    props: {
      label: 'Merge into target',
      search,
      itemKey: t => t.targetKey,
      itemLabel: t => t.targetName,
      ...props
    },
    attachTo: document.body
  })
  return { wrapper, search, input: wrapper.get('input') }
}

async function type(input, value) {
  input.element.value = value
  await input.trigger('input')
}

describe('Combobox.vue', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => {
    vi.useRealTimers()
    document.body.innerHTML = ''
  })

  it('has the APG combobox roles and attributes, collapsed at first', () => {
    const { wrapper, input } = mountCombobox()
    const listbox = wrapper.get('[role="listbox"]')
    expect(input.attributes('role')).toBe('combobox')
    expect(input.attributes('aria-label')).toBe('Merge into target')
    expect(input.attributes('aria-autocomplete')).toBe('list')
    expect(input.attributes('aria-expanded')).toBe('false')
    expect(input.attributes('aria-controls')).toBe(listbox.attributes('id'))
    expect(input.attributes('aria-activedescendant')).toBeUndefined()
  })

  it('debounces: searches once, 250 ms after the last keystroke', async () => {
    const { search, input } = mountCombobox()
    await type(input, 'we')
    vi.advanceTimersByTime(200)
    await type(input, 'web')
    vi.advanceTimersByTime(249)
    expect(search).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(search).toHaveBeenCalledTimes(1)
    expect(search).toHaveBeenCalledWith('web')
  })

  it('does not search below the minimum length', async () => {
    const { search, input, wrapper } = mountCombobox()
    await type(input, 'w')
    vi.advanceTimersByTime(1000)
    expect(search).not.toHaveBeenCalled()
    expect(wrapper.get('[role="status"]').text()).toBe('Type at least 2 characters.')
  })

  it('opens the listbox with the results and announces the count', async () => {
    const { wrapper, input } = mountCombobox()
    await type(input, 'web')
    vi.advanceTimersByTime(250)
    await flushPromises()
    expect(input.attributes('aria-expanded')).toBe('true')
    const options = wrapper.findAll('[role="option"]')
    expect(options.map(o => o.text())).toEqual(['web01', 'web02', 'web03'])
    expect(options.every(o => o.attributes('aria-selected') === 'false')).toBe(true)
    expect(wrapper.get('[role="status"]').text()).toBe('3 matches. Use Up and Down to choose.')
  })

  it('Down and Up move the active option (wrapping), Enter picks it', async () => {
    const { wrapper, input } = mountCombobox()
    await type(input, 'web')
    vi.advanceTimersByTime(250)
    await flushPromises()

    await input.trigger('keydown', { key: 'ArrowDown' })
    const options = () => wrapper.findAll('[role="option"]')
    expect(input.attributes('aria-activedescendant')).toBe(options()[0].attributes('id'))
    expect(options()[0].attributes('aria-selected')).toBe('true')

    await input.trigger('keydown', { key: 'ArrowUp' })
    expect(input.attributes('aria-activedescendant')).toBe(options()[2].attributes('id'))
    await input.trigger('keydown', { key: 'ArrowDown' })
    expect(input.attributes('aria-activedescendant')).toBe(options()[0].attributes('id'))
    await input.trigger('keydown', { key: 'ArrowDown' })

    await input.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('update:modelValue').at(-1)).toEqual([TARGETS[1]])
    expect(wrapper.emitted('select')).toEqual([[TARGETS[1]]])
    expect(input.element.value).toBe('web02')
    expect(input.attributes('aria-expanded')).toBe('false')
    expect(input.attributes('aria-activedescendant')).toBeUndefined()
  })

  it('Enter with no active option does not stop the form submitting', async () => {
    const { input } = mountCombobox()
    const event = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true })
    input.element.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(false)
  })

  it('Escape closes the list, a second Escape clears the text', async () => {
    const { wrapper, input } = mountCombobox()
    await type(input, 'web')
    vi.advanceTimersByTime(250)
    await flushPromises()

    await input.trigger('keydown', { key: 'Escape' })
    expect(input.attributes('aria-expanded')).toBe('false')
    expect(input.element.value).toBe('web')

    await input.trigger('keydown', { key: 'Escape' })
    expect(input.element.value).toBe('')
    expect(wrapper.emitted('update:modelValue')).toBeUndefined()
  })

  it('clicking an option picks it', async () => {
    const { wrapper, input } = mountCombobox()
    await type(input, 'web')
    vi.advanceTimersByTime(250)
    await flushPromises()
    await wrapper.findAll('[role="option"]')[2].trigger('click')
    expect(wrapper.emitted('update:modelValue').at(-1)).toEqual([TARGETS[2]])
    expect(input.element.value).toBe('web03')
  })

  it('typing after a pick clears the picked value', async () => {
    const { wrapper, input } = mountCombobox(undefined, { modelValue: TARGETS[0] })
    expect(input.element.value).toBe('web01')
    await type(input, 'web0')
    expect(wrapper.emitted('update:modelValue').at(-1)).toEqual([null])
  })

  it('an older, slower response never replaces a newer one', async () => {
    let resolveFirst
    const search = vi.fn()
      .mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve }))
      .mockImplementationOnce(() => Promise.resolve([TARGETS[2]]))
    const { wrapper, input } = mountCombobox(search)
    await type(input, 'we')
    vi.advanceTimersByTime(250)
    await type(input, 'web03')
    vi.advanceTimersByTime(250)
    await flushPromises()
    resolveFirst(TARGETS)
    await flushPromises()
    expect(wrapper.findAll('[role="option"]').map(o => o.text())).toEqual(['web03'])
  })

  it('shows "No matches." and stays collapsed when nothing matches; shows a search error', async () => {
    const search = vi.fn()
      .mockImplementationOnce(() => Promise.resolve([]))
      .mockImplementationOnce(() => Promise.reject(new Error('Search failed: 403')))
    const { wrapper, input } = mountCombobox(search)
    await type(input, 'zz')
    vi.advanceTimersByTime(250)
    await flushPromises()
    expect(input.attributes('aria-expanded')).toBe('false')
    expect(wrapper.get('[role="status"]').text()).toBe('No matches.')

    await type(input, 'zzz')
    vi.advanceTimersByTime(250)
    await flushPromises()
    expect(wrapper.get('[role="status"]').text()).toBe('Search failed: 403')
  })
})
