import { defineStore } from 'pinia'

// UI review W2-2: the last filter/sort/page a list page was showing, by route name, so the places that lead back to
// a list (a Save or Cancel on a detail page, the breadcrumb) return to the same view instead of an unfiltered page 1.
// Kept in memory only: a reload of the app starts clean, and so does opening the list from the menu.
export const useListStateStore = defineStore('listState', {
  state: () => ({ queries: {} }),
  getters: {
    /** The remembered query for a list's route name; {} when there is none. */
    queryFor: (state) => (routeName) => ({ ...(state.queries[routeName] ?? {}) })
  },
  actions: {
    remember(routeName, query) {
      this.queries[routeName] = { ...query }
    },
    forget(routeName) {
      delete this.queries[routeName]
    }
  }
})
