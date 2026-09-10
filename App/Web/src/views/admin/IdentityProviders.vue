<script setup>
// CRUD against /api/admin/identity-providers (IdentityProvidersController).
// WindowsIntegrated, DevFakeAuth, OIDC, and SAML are all wired at runtime
// now (D-84) -- OIDC's scheme is registered once at startup though
// (App/Api/Auth/AuthenticationExtensions.cs), so enabling/disabling it or
// changing its Authority/ClientId/secret here needs an app restart to take
// effect; SAML is read fresh on every request (Saml2ConfigurationFactory),
// so it needs no restart.
//
// D-95: OIDC/SAML's ConfigurationValues used to be a raw JSON textarea an
// admin had to hand-write against OidcProviderSettings/SamlProviderSettings'
// exact property names -- structured fields below, still serialized to the
// same ConfigurationValues JSON string on save (the API/database shape is
// unchanged; System.Text.Json's case-insensitive read means these camelCase
// keys deserialize the same as the PascalCase ones typed by hand before).
//
// D-124 Phase 4: Add/Edit (including the structured OIDC/SAML config field
// handling above) moved to its own routed page (IdentityProviderEdit.vue,
// admin-identity-provider-create/-edit) -- this page no longer owns an
// inline editing form.
import { ref, onMounted } from 'vue'

const providers = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/identity-providers')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    providers.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(provider) {
  const response = await fetch(`/api/admin/identity-providers/${provider.providerKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Identity Providers</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p><router-link :to="{ name: 'admin-identity-provider-create' }">+ New Provider</router-link></p>

      <table>
        <thead>
          <tr><th>Type</th><th>Display Name</th><th>Enabled</th><th>Order</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="provider in providers" :key="provider.providerKey">
            <td>{{ provider.providerType }}</td>
            <td>{{ provider.displayName }}</td>
            <td>{{ provider.isEnabled }}</td>
            <td>{{ provider.displayOrder }}</td>
            <td>
              <router-link :to="{ name: 'admin-identity-provider-edit', params: { providerKey: provider.providerKey } }">Edit</router-link>
              <button @click="remove(provider)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
