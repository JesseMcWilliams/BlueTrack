<script setup>
// D-100: real provider redirect logic, unblocked now that the Identity
// Providers admin screen exists and GET /api/auth/providers is real.
// D-41's "default provider" policy resolved 2026-09-05: lowest DisplayOrder
// among enabled providers wins, no new admin setting or per-user
// last-used tracking for now.
//
// Only OIDC/SAML need an actual browser redirect to an external IdP --
// WindowsIntegrated authenticates transparently on the next request
// (Negotiate is this app's default challenge scheme, AuthenticationExtensions.cs),
// and DevFakeAuth is a dev-only convenience (DevTestAuthController), never
// auto-triggered from a real login screen.
//
// D-173: "transparently on the next request" needs a next request. The
// router guard lands here after /api/me returned 401, and the rights store
// caches that answer. The browser may finish the Windows sign-in (silently,
// or after its own credentials prompt) without anything asking /api/me
// again, so the page used to sit here signed in. When the guard sent us
// (returnUrl present) and Windows Integrated is the default provider, ask
// /api/me again and go on to returnUrl once it succeeds. A direct visit to
// /login (no returnUrl) still just lists the providers.
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'

const providers = ref([])
const loading = ref(true)
const error = ref(null)
const signingIn = ref(false)
const windowsFailed = ref(false)
const route = useRoute()
const router = useRouter()
const rights = useRightsStore()

function returnUrl() {
  const value = route.query.returnUrl
  return typeof value === 'string' && value.startsWith('/') ? value : '/'
}

function externalRedirectUrl(provider) {
  const encoded = encodeURIComponent(returnUrl())
  if (provider.providerType === 'OIDC') return `/api/auth/login/oidc?returnUrl=${encoded}`
  if (provider.providerType === 'SAML') return `/api/auth/saml/login?returnUrl=${encoded}`
  return null
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

async function load() {
  loading.value = true
  let tryWindows = false
  try {
    const response = await fetch('/api/auth/providers')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    const data = await response.json()
    providers.value = [...data.providers].sort((a, b) => a.displayOrder - b.displayOrder)

    const defaultProvider = providers.value[0]
    const redirectUrl = defaultProvider ? externalRedirectUrl(defaultProvider) : null
    if (redirectUrl) {
      window.location.href = redirectUrl
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
            <span v-else>{{ provider.displayName }} (signs in automatically)</span>
            <button
              v-if="provider.providerType === 'WindowsIntegrated'"
              type="button"
              :disabled="signingIn"
              @click="windowsSignIn"
            >{{ signingIn ? 'Signing in...' : 'Continue' }}</button>
          </li>
        </ul>
      </template>
    </template>
  </div>
</template>
