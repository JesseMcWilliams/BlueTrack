<script setup>
// D-186: GET /api/reports/decom-accounts (web.vw_decom_account) --
// accounts in a safe flagged for deletion, or whose own name matches the
// account decommission pattern. An account not yet deleted gets a note; if
// the same account (username + address) is also in another, unflagged
// safe, the note names it -- it has probably moved there. Read-only, no
// permission gate.
import { ref, computed, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)
const hideDeleted = ref(false)

const shown = computed(() => (hideDeleted.value ? items.value.filter(i => !i.isDeleted) : items.value))

function why(item) {
  const reasons = []
  if (item.inFlaggedSafe) reasons.push('Safe flagged')
  if (item.nameFlagged) reasons.push('Name flagged')
  return reasons.join(', ')
}

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/decom-accounts')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
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
    <h2>Accounts Flagged for Deletion</h2>
    <p class="hint">
      Accounts in a <router-link :to="{ name: 'reports-decom-safes' }">safe flagged for deletion</router-link>, or whose own
      name matches the <strong>account decommission pattern</strong> (Admin &gt; Global Application Configuration).
      <strong>Not deleted yet</strong> marks accounts still active in CyberArk; <strong>Also in</strong> lists other safes
      holding an account with the same username and address.
    </p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load the report: {{ error }}</p>
    <template v-else>
      <p><label><input v-model="hideDeleted" type="checkbox" /> Only accounts not yet deleted</label></p>
      <p v-if="shown.length === 0">No accounts are flagged for deletion.</p>
      <table v-else>
        <thead>
          <tr><th>Username</th><th>Address</th><th>Account Name</th><th>Safe</th><th>Source</th><th>Flagged by</th><th>Note</th></tr>
        </thead>
        <tbody>
          <tr v-for="item in shown" :key="item.accountKey">
            <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: item.accountKey } }">{{ item.userName || item.accountName }}</router-link><span v-if="!item.userName" class="no-username"> (no username)</span></td>
            <td>{{ item.address }}</td>
            <td>{{ item.accountName }}</td>
            <td>{{ item.safeName }}</td>
            <td>{{ item.sourceSystemName }}</td>
            <td>{{ why(item) }}</td>
            <td>
              <template v-if="item.isDeleted">Deleted</template>
              <template v-else><strong>Not deleted yet.</strong></template>
              <template v-if="item.otherSafes"> Also in: {{ item.otherSafes }}</template>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.no-username {
  font-size: 0.85em;
  opacity: 0.8;
}
</style>
