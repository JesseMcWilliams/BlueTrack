import { reactive, readonly } from 'vue'

// UI review theme 3: a save that leaves the page (back to the list) used to say nothing, so the person had to infer
// from the list that it worked. A page calls `flash(text)` just before it navigates; the message shows once on the
// page it lands on (FlashMessage.vue, mounted in App.vue) and goes away at the next navigation, or when dismissed.
//
// A plain module-level reactive singleton, like useConfirmDialog.js, not a Pinia store: every edit page uses it, and
// a page that never needed Pinia (and its tests) shouldn't have to start.
const state = reactive({ message: null, shown: false })

/** Show `message` on the page the next navigation lands on. */
export function flash(message) {
  state.message = message
  state.shown = false
}

/** Called after every navigation: the first one after `flash` shows the message, the next one clears it. */
export function flashNavigated() {
  if (!state.message) return
  if (state.shown) flashDismiss()
  else state.shown = true
}

export function flashDismiss() {
  state.message = null
  state.shown = false
}

/** "{name} saved." for a named thing, or "Saved." when it has no name to give. */
export function savedMessage(t, name) {
  const text = name === null || name === undefined ? '' : String(name).trim()
  return text ? t('common.savedItem', { name: text }) : t('common.saved')
}

/** The current message, for the component that shows it. */
export function useFlashState() {
  return readonly(state)
}
