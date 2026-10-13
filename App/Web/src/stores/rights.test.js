import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useRightsStore } from './rights'
import { usePageSizeStore } from './pageSize'

function jsonResponse(body, ok = true, status = 200) {
  return {
    ok,
    status,
    json: () => Promise.resolve(body)
  }
}

// D-205: load() asks /api/auth/session first. `session` is its answer;
// every other request goes to `fetchMock`, which the tests below drive.
let fetchMock
let session

describe('rights store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    fetchMock = vi.fn()
    session = { authenticated: false, windows: true }
    globalThis.fetch = vi.fn((url, init) => url === '/api/auth/session'
      ? Promise.resolve(jsonResponse(session))
      : fetchMock(url, init))
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
      fetchMock.mockResolvedValueOnce(
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

    it('sets error and leaves loaded false on a failed (non-401) response', async () => {
      fetchMock.mockResolvedValueOnce(jsonResponse(null, false, 500))
      const store = useRightsStore()

      await store.load()

      expect(store.loaded).toBe(false)
      expect(store.loading).toBe(false)
      expect(store.error).toContain('500')
    })

    it('sets authenticated false and loaded true on a 401, without setting error', async () => {
      fetchMock.mockResolvedValueOnce(jsonResponse(null, false, 401))
      const store = useRightsStore()

      await store.load()

      expect(store.authenticated).toBe(false)
      expect(store.loaded).toBe(true)
      expect(store.loading).toBe(false)
      expect(store.error).toBeNull()
    })

    // D-124 Phase 3: load() wires the pageSize store's loadFromServer the
    // same way it already wires the theme store's -- same call site.
    it('applies preferences.PageSize to the pageSize store on success', async () => {
      fetchMock.mockResolvedValueOnce(
        jsonResponse({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [], preferences: { PageSize: '100' } })
      )
      const store = useRightsStore()

      await store.load()

      expect(usePageSizeStore().current).toBe(100)
    })

    it('sets authenticated true on success', async () => {
      fetchMock.mockResolvedValueOnce(
        jsonResponse({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [] })
      )
      const store = useRightsStore()

      await store.load()

      expect(store.authenticated).toBe(true)
    })
  })

  describe('ensureLoaded', () => {
    it('only calls load once for concurrent callers', async () => {
      fetchMock.mockResolvedValue(
        jsonResponse({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [] })
      )
      const store = useRightsStore()

      await Promise.all([store.ensureLoaded(), store.ensureLoaded(), store.ensureLoaded()])

      expect(fetchMock).toHaveBeenCalledTimes(1)
    })

    it('does not re-fetch once already loaded', async () => {
      fetchMock.mockResolvedValue(
        jsonResponse({ userKey: 1, displayName: 'A', roleNames: [], permissionNames: [] })
      )
      const store = useRightsStore()

      await store.ensureLoaded()
      await store.ensureLoaded()

      expect(fetchMock).toHaveBeenCalledTimes(1)
    })
  })

  describe('reload', () => {
    it('updates roleNames/permissionNames from /api/me/reload-rights', async () => {
      const store = useRightsStore()
      store.roleNames = ['Viewer']
      store.permissionNames = ['ViewDashboard']
      fetchMock.mockResolvedValueOnce(
        jsonResponse({ roleNames: ['Approver'], permissionNames: ['ViewDashboard', 'ApproveExceptions'] })
      )

      await store.reload()

      expect(store.roleNames).toEqual(['Approver'])
      expect(store.hasPermission('ApproveExceptions')).toBe(true)
      expect(fetchMock).toHaveBeenCalledWith('/api/me/reload-rights', { method: 'POST' })
    })

    it('sets error and leaves roleNames/permissionNames unchanged on a failed response', async () => {
      const store = useRightsStore()
      store.roleNames = ['Viewer']
      store.permissionNames = ['ViewDashboard']
      fetchMock.mockResolvedValueOnce(jsonResponse(null, false, 403))

      await store.reload()

      expect(store.error).toContain('403')
      expect(store.roleNames).toEqual(['Viewer'])
      expect(store.permissionNames).toEqual(['ViewDashboard'])
    })
  })

  // D-205: without a session and with Windows not wanted, /api/me (and its
  // silent Windows challenge) is never called.
  describe('session check', () => {
    it('skips /api/me and reports not authenticated when Windows sign-in is not wanted', async () => {
      session = { authenticated: false, windows: false }
      const store = useRightsStore()
      await store.ensureLoaded()

      expect(store.authenticated).toBe(false)
      expect(store.loaded).toBe(true)
      expect(fetchMock).not.toHaveBeenCalled()
    })

    it('calls /api/me when there is already a session, even if Windows is not wanted', async () => {
      session = { authenticated: true, windows: false }
      fetchMock.mockResolvedValueOnce(jsonResponse({ userKey: 7, displayName: 'SAML User', roleNames: ['Analyst'], permissionNames: [], preferences: {} }))
      const store = useRightsStore()
      await store.ensureLoaded()

      expect(fetchMock).toHaveBeenCalledWith('/api/me', undefined)
      expect(store.authenticated).toBe(true)
      expect(store.displayName).toBe('SAML User')
    })
  })
})
