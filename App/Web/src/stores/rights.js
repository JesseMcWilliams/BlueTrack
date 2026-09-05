import { defineStore } from 'pinia'
import { useThemeStore } from './theme'

// Backs frontend permission-aware UI: hides/disables controls a user can't
// use, mirroring the API's real [Authorize(Policy = Permissions.X)] gates
// (App/Api/Auth/Permissions.cs) rather than duplicating a second copy of
// the permission catalog here. This is presentation only -- the API is the
// actual enforcement point regardless of what this store says.
export const useRightsStore = defineStore('rights', {
  state: () => ({
    userKey: null,
    displayName: null,
    roleNames: [],
    permissionNames: [],
    loaded: false,
    loading: false,
    error: null,
    _loadPromise: null
  }),
  getters: {
    hasPermission: (state) => (name) => state.permissionNames.includes(name)
  },
  actions: {
    // Vue mounts children before parents, so a child route's onMounted can
    // run before App.vue's own onMounted (which calls this) -- callers that
    // need permissions before rendering should await ensureLoaded() rather
    // than assume some ancestor already populated the store. Dedupes
    // concurrent callers onto one in-flight fetch instead of one each.
    ensureLoaded() {
      if (this.loaded) return Promise.resolve()
      if (!this._loadPromise) this._loadPromise = this.load()
      return this._loadPromise
    },
    async load() {
      this.loading = true
      this.error = null
      try {
        const response = await fetch('/api/me')
        if (!response.ok) throw new Error(`Request failed: ${response.status}`)
        const data = await response.json()
        this.userKey = data.userKey
        this.displayName = data.displayName
        this.roleNames = data.roleNames
        this.permissionNames = data.permissionNames
        this.loaded = true
        useThemeStore().loadFromServer(data.preferences)
      } catch (err) {
        this.error = err.message
      } finally {
        this.loading = false
        this._loadPromise = null
      }
    },
    // Self-service "Reload My Rights" (D-14) -- re-resolves group membership
    // and permissions live. See PermissionClaimsTransformation's own
    // comment: every request is already a live resolution (no session cache
    // exists yet), so this doesn't change server-side behavior, but it does
    // give the user a fresh, explicit result after they know they were just
    // added to a new group, without waiting for anything to expire.
    async reload() {
      this.loading = true
      this.error = null
      try {
        const response = await fetch('/api/me/reload-rights', { method: 'POST' })
        if (!response.ok) throw new Error(`Request failed: ${response.status}`)
        const data = await response.json()
        this.roleNames = data.roleNames
        this.permissionNames = data.permissionNames
      } catch (err) {
        this.error = err.message
      } finally {
        this.loading = false
      }
    }
  }
})
