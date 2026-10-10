import { defineStore } from 'pinia'

// D-182 / D-190: a list page's bulk-action selection, one store per list
// ('accounts', 'targets', 'accessGroups', 'riskExceptions'). A store rather
// than page state, so the selection survives paging, sorting, filtering and
// leaving the page (e.g. to Account Progress's Bulk Edit page). Kept for
// this browser session only.
const definitions = {}

export function useListSelectionStore(listId) {
  definitions[listId] ??= defineStore(`selection-${listId}`, {
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
  return definitions[listId]()
}
