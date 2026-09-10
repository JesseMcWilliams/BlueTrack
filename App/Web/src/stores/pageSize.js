import { defineStore } from 'pinia'

// D-124 Phase 3: user-configurable page size for the six paginated list
// pages (Targets/AccessGroups/AccountProgressList/RiskExceptionsList/
// AuditLogViewer/RiskScoreReport), persisted server-side via the same
// generic web.user_preference key/value store Theme already uses (D-93) --
// so it follows the user across machines/browsers, not just this browser.
// Mirrors theme.js's shape: loadFromServer() reads preferences?.PageSize
// off the /api/me response (rights.js calls this the same place it already
// calls useThemeStore().loadFromServer), and setPageSize() optimistically
// applies + PUTs the new value. Unlike Theme, this doesn't need an
// instant-default-before-server-load step (initBeforeServerLoad) -- page
// size doesn't affect anything visible before a list page's own first load,
// so there's no flash-of-wrong-value to avoid the way Theme has one.
const DEFAULT_PAGE_SIZE = 50
// D-124 Phase 3: server-side cap (PagingParams.MaxPageSize in the API) is
// 500 -- these are just the reasonable dropdown choices, matching the
// pager's own <select> options.
export const PAGE_SIZE_OPTIONS = [25, 50, 100, 250]

function parsePageSize(value) {
  const parsed = parseInt(value, 10)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_PAGE_SIZE
}

export const usePageSizeStore = defineStore('pageSize', {
  state: () => ({
    current: DEFAULT_PAGE_SIZE
  }),
  actions: {
    // Called once /api/me's preferences are available (rights store) --
    // same call site as useThemeStore().loadFromServer.
    loadFromServer(preferences) {
      const serverValue = preferences?.PageSize
      if (serverValue === undefined || serverValue === null) return
      this.current = parsePageSize(serverValue)
    },
    async setPageSize(size) {
      const parsed = parsePageSize(size)
      this.current = parsed

      await fetch('/api/me/preferences/PageSize', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: String(parsed) })
      })
    }
  }
})
