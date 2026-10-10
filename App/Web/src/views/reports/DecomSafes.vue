<script setup>
// D-186: GET /api/reports/decom-safes (web.vw_decom_safe) -- safes whose
// name matches the safe decommission pattern on Global Application
// Configuration. Ignored safes aren't listed. Read-only, no permission gate.
import { ref, onMounted } from 'vue'

const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/decom-safes')
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
    <h2>Safes Flagged for Deletion</h2>
    <p class="hint">
      Safes whose name matches the <strong>safe decommission pattern</strong> (Admin &gt; Global Application Configuration),
      typically a prefix or suffix such as <code>DEL_</code> or <code>_DECOM</code> added while CyberArk's retention policy
      keeps the safe, or while its accounts move to another safe. Accounts still in these safes are on
      <router-link :to="{ name: 'reports-decom-accounts' }">Accounts Flagged for Deletion</router-link>.
    </p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load the report: {{ error }}</p>
    <p v-else-if="items.length === 0">No safes are flagged for deletion. If you expected some, check the safe decommission pattern.</p>
    <table v-else>
      <thead>
        <tr><th>Safe</th><th>Source</th><th>Application</th><th>Accounts not yet deleted</th><th>Deleted accounts</th></tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.safeKey">
          <td>{{ item.safeName }}</td>
          <td>{{ item.sourceSystemName }}</td>
          <td>{{ item.applicationName }}</td>
          <td>{{ item.activeAccountCount }}</td>
          <td>{{ item.deletedAccountCount }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
