import { useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'

// D-206/D-209: signing out, shared by the user menu's Sign out and the idle
// timer. Ends the server sessions, forgets this tab's rights and shows the
// sign-in page, which then doesn't start the default sign-in method again.
// `reason` ('idle') changes the sign-in page's message. Throws if the server
// refuses (a 401 counts as already signed out).
export function useSignOut() {
  const router = useRouter()
  const rights = useRightsStore()

  return async function signOut(reason) {
    const response = await fetch('/api/auth/logout', { method: 'POST' })
    if (!response.ok && response.status !== 401) {
      throw new Error(`Sign out failed: ${response.status}`)
    }
    rights.$reset()
    await router.push({ name: 'login', query: reason ? { signedOut: '1', reason } : { signedOut: '1' } })
  }
}
