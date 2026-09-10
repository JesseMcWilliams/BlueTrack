import { reactive } from 'vue'

// D-128: a single, app-wide confirmation dialog -- every Delete button
// across the app awaits confirmDelete(...) before actually deleting,
// rather than each of the 10 pages with one building/styling its own
// dialog. One reactive singleton state, one <ConfirmDialog /> instance
// mounted once in App.vue (mirrors this app's existing single-instance
// shared-UI pattern, e.g. Pager.vue/FilterCountSummary.vue being reused
// rather than copied, just promise-driven instead of prop-driven since
// only one confirmation can ever be pending at a time).
const state = reactive({
  visible: false,
  message: '',
  confirmLabel: 'Delete'
})

let pendingResolve = null

/** Shows the shared dialog with `message`; resolves true on Confirm, false on Cancel/Escape. */
export function confirmDelete(message, confirmLabel = 'Delete') {
  state.message = message
  state.confirmLabel = confirmLabel
  state.visible = true
  return new Promise((resolve) => {
    pendingResolve = resolve
  })
}

/** Called only by ConfirmDialog.vue itself. */
export function respondToConfirmDialog(result) {
  state.visible = false
  if (pendingResolve) {
    pendingResolve(result)
    pendingResolve = null
  }
}

export function useConfirmDialogState() {
  return state
}
