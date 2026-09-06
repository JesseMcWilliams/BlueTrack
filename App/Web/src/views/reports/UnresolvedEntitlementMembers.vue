<script setup>
// Calls GET /api/reports/unresolved-entitlement-members (ReportsController),
// backed by dbo.vw_unresolved_entitlement_members -- Safe entitlements
// granted to a CyberArk Identity/Entra-federated user or built-in cloud
// role rather than a classic Vault-native user/group, so this app has no
// display name to show (D-107/D-108). Read-only, no permission gate.
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/unresolved-entitlement-members')
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>Unresolved Entitlement Members</h2>
    <p class="hint">
      These Safe members are not linked to a known BlueTrack user or group -- typically an internal
      or service identity, such as a built-in CyberArk cloud role or a CyberArk Identity/Entra-federated
      principal that doesn't appear in the standard Users/Groups exports. The raw identifier is shown
      below since this app has no display name to resolve it to.
    </p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load unresolved entitlement members: {{ error }}</p>
    <p v-else-if="items.length === 0">No unresolved entitlement members found.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Safe</th>
          <th>Member Type</th>
          <th>Unresolved Identifier</th>
          <th>Membership Expiration</th>
          <th>Snapshot Date</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.entitlementKey">
          <td>{{ item.safeName }}</td>
          <td>{{ item.memberType }}</td>
          <td>
            <span class="unresolved-flag">Unresolved:</span>
            {{ item.unresolvedMemberId }}
          </td>
          <td>{{ item.membershipExpirationDate }}</td>
          <td>{{ item.snapshotDate }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
  margin-bottom: 1rem;
}

.unresolved-flag {
  font-weight: bold;
  color: var(--color-error-text);
}
</style>
