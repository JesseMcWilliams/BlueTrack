import { describe, it, expect, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useListStateStore } from './listState'

// UI review W2-2: the last view of each list, by route name.
describe('listState store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('remembers a list\'s query by route name, and gives {} for a list it has not seen', () => {
    const store = useListStateStore()
    store.remember('account-progress-list', { stage: 'Discovery', page: '2' })

    expect(store.queryFor('account-progress-list')).toEqual({ stage: 'Discovery', page: '2' })
    expect(store.queryFor('targets')).toEqual({})
  })

  it('hands out copies, so a caller cannot change what is remembered', () => {
    const store = useListStateStore()
    const query = { stage: 'Discovery' }
    store.remember('account-progress-list', query)
    query.stage = 'Changed'
    store.queryFor('account-progress-list').stage = 'Also changed'

    expect(store.queryFor('account-progress-list')).toEqual({ stage: 'Discovery' })
  })

  it('replaces the earlier query, and forgets on request', () => {
    const store = useListStateStore()
    store.remember('account-progress-list', { stage: 'A' })
    store.remember('account-progress-list', {})

    expect(store.queryFor('account-progress-list')).toEqual({})

    store.remember('account-progress-list', { stage: 'B' })
    store.forget('account-progress-list')
    expect(store.queryFor('account-progress-list')).toEqual({})
  })
})
