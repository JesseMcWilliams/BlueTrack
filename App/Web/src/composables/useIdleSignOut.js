import { onMounted, onBeforeUnmount } from 'vue'
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

export function useIdleSignOut({ now = () => Date.now(), checkEveryMs = 30000 } = {}) {
  const rights = useRightsStore()
  const signOut = useSignOut()
  let lastActivity = now()
  let lastShared = 0
  let signingOut = false
  let timer = null

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
    if (lastActivity - lastShared >= SHARE_EVERY_MS) {
      lastShared = lastActivity
      try { localStorage.setItem(LAST_ACTIVITY_KEY, String(lastActivity)) } catch { /* private window: this tab only */ }
    }
  }

  async function check() {
    const minutes = rights.idleTimeoutMinutes
    if (!rights.authenticated || !minutes || signingOut) return
    if (now() - sharedLastActivity() <= minutes * 60000) return
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

  return { touch, check }
}
