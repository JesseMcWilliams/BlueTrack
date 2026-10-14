import { useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import { navigateAway } from '../utils/navigateAway'
import { t } from '../i18n'

// D-206/D-209: signing out, shared by the user menu's Sign out and the idle
// timer. Ends the server sessions, forgets this tab's rights and shows the
// sign-in page, which then doesn't start the default sign-in method again.
// `reason` ('idle') changes the sign-in page's message. Throws if the server
// refuses (a 401 counts as already signed out). D-217: for a SAML session the
// API may answer with a Single Logout address, which the browser goes to.
export function useSignOut() {
  const router = useRouter()
  const rights = useRightsStore()

  return async function signOut(reason) {
    const response = await fetch('/api/auth/logout', { method: 'POST' })
    if (!response.ok && response.status !== 401) {
      throw new Error(t('shell.userMenu.signOutFailed', { status: response.status }))
    }
    rights.$reset()
    // D-217: a SAML session signs out at the identity provider too (Single
    // Logout). That's a browser round trip, so leave the page; the API sends
    // the browser back to the sign-in page afterwards.
    const body = response.status === 200 ? await response.json().catch(() => null) : null
    if (body?.singleLogoutUrl) {
      navigateAway(reason ? `${body.singleLogoutUrl}?reason=${encodeURIComponent(reason)}` : body.singleLogoutUrl)
      return
    }
    await router.push({ name: 'login', query: reason ? { signedOut: '1', reason } : { signedOut: '1' } })
  }
}
