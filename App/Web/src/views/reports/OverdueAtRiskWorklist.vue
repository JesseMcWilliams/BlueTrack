<script setup>
// Calls GET /api/reports/overdue-at-risk (ReportsController) -- accounts
// past TargetRemediationDate that aren't yet complete (D-56).
import { ref, onMounted } from 'vue'

const accounts = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/overdue-at-risk')
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    accounts.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>Overdue / At-Risk Worklist</h2>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">Could not load accounts: {{ error }}</p>
    <p v-else-if="accounts.length === 0">No accounts are past their target remediation date.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Account</th>
          <th>Stage</th>
          <th>Status</th>
          <th>Risk Level</th>
          <th>Owner</th>
          <th>Target Remediation Date</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="account in accounts"
          :key="account.accountKey"
          @click="$router.push({ name: 'account-progress-detail', params: { accountKey: account.accountKey } })"
        >
          <td>{{ account.accountName }}</td>
          <td>{{ account.stageName }}</td>
          <td>{{ account.statusName }}</td>
          <td>{{ account.riskLevelName }}</td>
          <td>{{ account.ownerName }}</td>
          <td>{{ account.targetRemediationDate }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
