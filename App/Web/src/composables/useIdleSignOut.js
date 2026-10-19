import { ref, onMounted, onBeforeUnmount } from 'vue'
import { useRightsStore } from '../stores/rights'
import { useSignOut } from './useSignOut'

// D-209: signs the user out after Global Application Configuration's Idle
// Timeout (rights.idleTimeoutMinutes, from /api/auth/session) without
// activity. Activity in any BlueTrack tab counts: the last time is shared
// through localStorage, so a user busy in one tab isn't signed out of
// another. The server also ends idle OIDC/SAML sessions (IdleSessionValidator),
// for a tab that was closed. Used once, by App.vue.
export const LAST_ACTIVITY_KEY = 'bluetrack.lastActivity'
const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'wheel', 'touchstart', 'mousemove']
const SHARE_EVERY_MS = 5000
// UI review W12-1: a warning shows for the last two minutes before the sign-out (or the last half of a timeout shorter
// than four minutes), so a user part-way through a form can act before the page is taken away.
const WARN_MS = 120000

export function useIdleSignOut({ now = () => Date.now(), checkEveryMs = 30000 } = {}) {
  const rights = useRightsStore()
  const signOut = useSignOut()
  let lastActivity = now()
  let lastShared = 0
  let signingOut = false
  let timer = null
  // null, or { minutesLeft }: shown by IdleWarning.vue in App.vue. Any activity (including its Stay signed in button) clears it.
  const warning = ref(null)

  function sharedLastActivity() {
    try {
      const stored = Number(localStorage.getItem(LAST_ACTIVITY_KEY))
      return Number.isFinite(stored) && stored > lastActivity ? stored : lastActivity
    } catch {
      return lastActivity
    }
  }

  function touch() {
    lastActivity = now()
    warning.value = null
    if (lastActivity - lastShared >= SHARE_EVERY_MS) {
      lastShared = lastActivity
      try { localStorage.setItem(LAST_ACTIVITY_KEY, String(lastActivity)) } catch { /* private window: this tab only */ }
    }
  }

  async function check() {
    const minutes = rights.idleTimeoutMinutes
    if (!rights.authenticated || !minutes || signingOut) return
    const remaining = minutes * 60000 - (now() - sharedLastActivity())
    if (remaining > 0) {
      warning.value = remaining <= Math.min(WARN_MS, minutes * 30000) ? { minutesLeft: Math.max(1, Math.ceil(remaining / 60000)) } : null
      return
    }
    warning.value = null
    signingOut = true
    try {
      await signOut('idle')
    } catch {
      // The next check tries again.
    } finally {
      signingOut = false
      lastActivity = now()
    }
  }

  onMounted(() => {
    ACTIVITY_EVENTS.forEach(e => window.addEventListener(e, touch, { passive: true }))
    touch()
    timer = setInterval(check, checkEveryMs)
  })
  onBeforeUnmount(() => {
    ACTIVITY_EVENTS.forEach(e => window.removeEventListener(e, touch))
    clearInterval(timer)
  })

  return { touch, check, warning }
}
