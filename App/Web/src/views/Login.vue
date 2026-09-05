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
import { ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'

const providers = ref([])
const loading = ref(true)
const error = ref(null)
const route = useRoute()

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

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/auth/providers')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    const list = await response.json()
    providers.value = [...list].sort((a, b) => a.displayOrder - b.displayOrder)

    const defaultProvider = providers.value[0]
    const redirectUrl = defaultProvider ? externalRedirectUrl(defaultProvider) : null
    if (redirectUrl) {
      window.location.href = redirectUrl
      return
    }
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<template>
  <div>
    <h1>Sign in</h1>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p v-if="providers.length === 0">No identity providers are enabled. Contact an administrator.</p>
      <template v-else>
        <p>Choose a sign-in method:</p>
        <ul>
          <li v-for="provider in providers" :key="provider.providerType">
            <a v-if="externalRedirectUrl(provider)" :href="externalRedirectUrl(provider)">{{ provider.displayName }}</a>
            <span v-else>{{ provider.displayName }} (signs in automatically)</span>
          </li>
        </ul>
      </template>
    </template>
  </div>
</template>
