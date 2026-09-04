import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRightsStore } from './rights'

function jsonResponse(body, ok = true, status = 200) {
  return {
    ok,
    status,
    json: () => Promise.resolve(body)
  }
}

describe('rights store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    globalThis.fetch = vi.fn()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  describe('hasPermission', () => {
    it('returns false before any permissions are loaded', () => {
      const store = useRightsStore()

      expect(store.hasPermission('ApproveExceptions')).toBe(false)
    })

    it('returns true only for permissions present in permissionNames', () => {
      const store = useRightsStore()
      store.permissionNames = ['ViewDashboard', 'ApproveExceptions']

      expect(store.hasPermission('ApproveExceptions')).toBe(true)
      expect(store.hasPermission('ManageRolesAndPermissions')).toBe(false)
    })
  })

  describe('load', () => {
    it('populates state from /api/me on success', async () => {
      globalThis.fetch.mockResolvedValueOnce(
        jsonResponse({
          userKey: 42,
          displayName: 'Test Approver',
          roleNames: ['Approver'],
          permissionNames: ['ViewDashboard', 'ApproveExceptions']
        })
      )
      const store = useRightsStore()

      await store.load()

      expect(store.loaded).toBe(true)
      expect(store.loading).toBe(false)
      expect(store.error).toBeNull()
      expect(store.userKey).toBe(42)
      expect(store.roleNames).toEqual(['Approver'])
      expect(store.hasPermission('ApproveExceptions')).toBe(true)
    })

    it('sets error and leaves loaded false on a failed response', async () => {
      globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, false, 401))
      const store = useRightsStore()

      await store.load()

      expect(store.loaded).toBe(false)
      expect(store.loading).toBe(false)
      expect(store.error).toContain('401')
    })
  })

  describe('ensureLoaded', () => {
    it('only calls load once for concurrent callers', async () => {
      globalThis.fetch.mockResolvedValue(
        jsonResponse({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [] })
      )
      const store = useRightsStore()

      await Promise.all([store.ensureLoaded(), store.ensureLoaded(), store.ensureLoaded()])

      expect(globalThis.fetch).toHaveBeenCalledTimes(1)
    })

    it('does not re-fetch once already loaded', async () => {
      globalThis.fetch.mockResolvedValue(
        jsonResponse({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [] })
      )
      const store = useRightsStore()

      await store.ensureLoaded()
      await store.ensureLoaded()

      expect(globalThis.fetch).toHaveBeenCalledTimes(1)
    })
  })

  describe('reload', () => {
    it('updates roleNames/permissionNames from /api/me/reload-rights', async () => {
      const store = useRightsStore()
      store.roleNames = ['Viewer']
      store.permissionNames = ['ViewDashboard']
      globalThis.fetch.mockResolvedValueOnce(
        jsonResponse({ roleNames: ['Approver'], permissionNames: ['ViewDashboard', 'ApproveExceptions'] })
      )

      await store.reload()

      expect(store.roleNames).toEqual(['Approver'])
      expect(store.hasPermission('ApproveExceptions')).toBe(true)
      expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/reload-rights', { method: 'POST' })
    })
  })
})
