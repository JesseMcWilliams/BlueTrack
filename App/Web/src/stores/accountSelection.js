import { defineStore } from 'pinia'

// D-182: the Account Progress bulk-edit selection. A store rather than
// page state, so the selection survives paging, sorting, filtering and the
// trip to the Bulk Edit page and back. Kept for this browser session only;
// not persisted.
export const useAccountSelectionStore = defineStore('accountSelection', {
  state: () => ({
    enabled: false,
    keys: []
  }),
  getters: {
    count: (state) => state.keys.length,
    isSelected: (state) => (key) => state.keys.includes(key)
  },
  actions: {
    setEnabled(enabled) {
      this.enabled = enabled
      if (!enabled) this.keys = []
    },
    toggle(key) {
      this.keys = this.keys.includes(key) ? this.keys.filter(k => k !== key) : [...this.keys, key]
    },
    /** Adds keys not already selected. */
    add(keys) {
      const current = new Set(this.keys)
      this.keys = [...this.keys, ...keys.filter(k => !current.has(k))]
    },
    /** Flips each of these keys (the current page); keys elsewhere are untouched. */
    invert(keys) {
      const onPage = new Set(keys)
      const kept = this.keys.filter(k => !onPage.has(k))
      const current = new Set(this.keys)
      this.keys = [...kept, ...keys.filter(k => !current.has(k))]
    },
    clear() {
      this.keys = []
    }
  }
})
