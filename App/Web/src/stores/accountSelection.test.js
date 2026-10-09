import { describe, it, expect, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useAccountSelectionStore } from './accountSelection'

// D-182: the Account Progress bulk-edit selection.
describe('accountSelection store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('toggles single keys and counts them', () => {
    const store = useAccountSelectionStore()
    store.toggle(1)
    store.toggle(2)
    store.toggle(1)
    expect(store.keys).toEqual([2])
    expect(store.count).toBe(1)
    expect(store.isSelected(2)).toBe(true)
  })

  it('add keeps existing keys and skips duplicates', () => {
    const store = useAccountSelectionStore()
    store.add([1, 2])
    store.add([2, 3])
    expect(store.keys).toEqual([1, 2, 3])
  })

  it('invert flips only the given (current page) keys', () => {
    const store = useAccountSelectionStore()
    store.add([1, 2, 9]) // 9 is on another page
    store.invert([1, 2, 3, 4])
    expect([...store.keys].sort()).toEqual([3, 4, 9])
  })

  it('turning select mode off clears the selection', () => {
    const store = useAccountSelectionStore()
    store.setEnabled(true)
    store.add([1, 2])
    store.setEnabled(false)
    expect(store.enabled).toBe(false)
    expect(store.count).toBe(0)
  })
})
