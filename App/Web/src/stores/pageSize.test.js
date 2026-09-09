import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { usePageSizeStore } from './pageSize'

// D-124 Phase 3: mirrors theme.test.js's own coverage shape for the
// equivalent store -- default 50, reads preferences?.PageSize off the
// /api/me response, and PUTs on change. Unlike Theme, this has no
// initBeforeServerLoad/localStorage step (page size doesn't need to apply
// before first paint), so there's nothing to cover there.
describe('pageSize store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    globalThis.fetch = vi.fn().mockResolvedValue({ ok: true })
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('defaults to 50 before any server value is loaded', () => {
    const store = usePageSizeStore()
    expect(store.current).toBe(50)
  })

  describe('loadFromServer', () => {
    it('applies a valid numeric server-provided PageSize', () => {
      const store = usePageSizeStore()

      store.loadFromServer({ PageSize: '100' })

      expect(store.current).toBe(100)
    })

    it('falls back to 50 when the server value fails to parse', () => {
      const store = usePageSizeStore()

      store.loadFromServer({ PageSize: 'not-a-number' })

      expect(store.current).toBe(50)
    })

    it('does nothing when the preference is absent', () => {
      const store = usePageSizeStore()
      store.current = 250

      store.loadFromServer({})

      expect(store.current).toBe(250)
    })

    it('does nothing when preferences itself is absent', () => {
      const store = usePageSizeStore()
      store.current = 250

      store.loadFromServer(undefined)

      expect(store.current).toBe(250)
    })
  })

  describe('setPageSize', () => {
    it('applies the size and PUTs it to the server as a string', async () => {
      const store = usePageSizeStore()

      await store.setPageSize(100)

      expect(store.current).toBe(100)
      expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/preferences/PageSize', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: '100' })
      })
    })

    it('accepts a string value from a <select>, coercing it to a number', async () => {
      const store = usePageSizeStore()

      await store.setPageSize('250')

      expect(store.current).toBe(250)
    })

    it('falls back to 50 for an unparseable value rather than storing garbage', async () => {
      const store = usePageSizeStore()

      await store.setPageSize('not-a-number')

      expect(store.current).toBe(50)
      expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/preferences/PageSize', expect.objectContaining({
        body: JSON.stringify({ value: '50' })
      }))
    })
  })
})
