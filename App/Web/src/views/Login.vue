<script setup>
import { apiUrl } from '../utils/apiBase'
// D-100: real provider redirect logic against GET /api/auth/providers.
//
// D-205 (2026-10-12): the default sign-in method is an explicit setting
// (Identity Providers page; `defaultProviderType`, null = none) rather than
// the lowest DisplayOrder (D-41's old rule):
//   - /login with a default: OIDC/SAML redirect to the IdP straight away;
//     Windows signs in as before (D-173, below).
//   - /login with no default: lists the enabled methods.
//   - /login/windows, /login/oidc, /login/saml: that method, whatever the
//     default -- the address an IdP's launch URL or an admin's link uses.
// Choosing Windows while it isn't the default goes through
// /api/auth/login/windows, which records the choice (a session cookie) so the
// rights store's next /api/me does the Windows sign-in; otherwise the store
// never calls /api/me without a session (the browser would answer its Windows
// challenge silently).
//
// D-173: the router guard lands here after /api/me returned 401, and the
// rights store caches that answer. The browser may finish the Windows sign-in
// (silently, or after its own credentials prompt) without anything asking
// /api/me again, so the page used to sit here signed in. When the guard sent
// us (returnUrl present) and Windows is the default, ask /api/me again and go
// on to returnUrl once it succeeds.
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'

const PROVIDER_BY_PATH = { windows: 'WindowsIntegrated', oidc: 'OIDC', saml: 'SAML' }

const providers = ref([])
const defaultProviderType = ref(null)
const loading = ref(true)
const error = ref(null)
const signingIn = ref(false)
const windowsFailed = ref(false)
const route = useRoute()
const router = useRouter()
const rights = useRightsStore()

const requestedType = computed(() => PROVIDER_BY_PATH[route.params.provider] ?? null)
const signedOut = computed(() => route.query.signedOut === '1')

function returnUrl() {
  const value = route.query.returnUrl
  // D-197: a path on this site only -- "//host" and "/\host" are other sites to a browser.
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/'
}

function externalRedirectUrl(provider) {
  const encoded = encodeURIComponent(returnUrl())
  if (provider.providerType === 'OIDC') return apiUrl(`/api/auth/login/oidc?returnUrl=${encoded}`)
  if (provider.providerType === 'SAML') return apiUrl(`/api/auth/saml/login?returnUrl=${encoded}`)
  return null
}

// Windows only when it's the default (otherwise it takes a click); DevFakeAuth is dev/E2E only.
function signsInAutomatically(provider) {
  return provider.providerType === 'DevFakeAuth' ||
    (provider.providerType === 'WindowsIntegrated' && defaultProviderType.value === 'WindowsIntegrated')
}

async function windowsSignIn() {
  signingIn.value = true
  try {
    // Drop the cached "not signed in" so this asks /api/me again.
    rights.$patch({ loaded: false })
    await rights.ensureLoaded()
    if (rights.authenticated) {
      await router.replace(returnUrl())
    } else {
      windowsFailed.value = true
    }
  } finally {
    signingIn.value = false
  }
}

function start(provider) {
  const redirectUrl = externalRedirectUrl(provider)
  if (redirectUrl) {
    window.location.href = redirectUrl
    return
  }
  if (provider.providerType === 'WindowsIntegrated') {
    if (defaultProviderType.value === 'WindowsIntegrated') {
      windowsSignIn()
    } else {
      window.location.href = apiUrl(`/api/auth/login/windows?returnUrl=${encodeURIComponent(returnUrl())}`)
    }
  }
}

async function load() {
  loading.value = true
  let tryWindows = false
  try {
    const response = await fetch('/api/auth/providers')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    const data = await response.json()
    providers.value = [...data.providers].sort((a, b) => a.displayOrder - b.displayOrder)
    defaultProviderType.value = data.defaultProviderType ?? null

    if (requestedType.value) {
      const requested = providers.value.find(p => p.providerType === requestedType.value)
      if (requested) start(requested)
      else error.value = "That sign-in method isn't enabled. Choose one below."
      return
    }

    // D-206: just signed out -- list the methods; don't start the default again.
    if (signedOut.value) return

    const defaultProvider = providers.value.find(p => p.providerType === defaultProviderType.value)
    if (defaultProvider && externalRedirectUrl(defaultProvider)) {
      start(defaultProvider)
      return
    }
    tryWindows = defaultProvider?.providerType === 'WindowsIntegrated' && typeof route.query.returnUrl === 'string'
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
  if (tryWindows) await windowsSignIn()
}

onMounted(load)
</script>

<template>
  <div>
    <h1>Sign in</h1>
    <p v-if="signedOut" role="status">You're signed out.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-if="windowsFailed && !signingIn" role="alert">
      Windows sign-in didn't complete. If your browser asked for credentials, check them and choose Continue again.
    </p>

    <template v-else>
      <p v-if="providers.length === 0">No identity providers are enabled. Contact an administrator.</p>
      <template v-else>
        <p>Choose a sign-in method:</p>
        <ul>
          <li v-for="provider in providers" :key="provider.providerType">
            <a v-if="externalRedirectUrl(provider)" :href="externalRedirectUrl(provider)">{{ provider.displayName }}</a>
            <span v-else-if="signsInAutomatically(provider)">{{ provider.displayName }} (signs in automatically)</span>
            <span v-else>{{ provider.displayName }}</span>
            <span v-if="provider.providerType === defaultProviderType"> (default)</span>
            <button
              v-if="provider.providerType === 'WindowsIntegrated'"
              type="button"
              :disabled="signingIn"
              @click="start(provider)"
            >{{ signingIn ? 'Signing in...' : 'Continue' }}</button>
          </li>
        </ul>
      </template>
    </template>
  </div>
</template>
