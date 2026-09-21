<script setup>
// Calls GET /api/reports/risk-exception-sod (ReportsController), gated by
// ViewRiskExceptionSodReport. Detective/audit report (Option C): every
// historical case of the same user both approving a Risk Exception and
// linking it to an account, built entirely from the existing audit trail --
// reported regardless of whether the enforcement toggle on the Global
// Application Configuration page is (or ever was) on.
import { ref, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'

const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/risk-exception-sod')
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
    <h2>Risk Exception Segregation of Duties</h2>
    <p class="hint">
      Cases where the same person both approved a Risk Exception and later linked it to an account's
      progress record. Shown here regardless of whether "Enforce segregation of duties on Risk Exception
      approval" is currently enabled on Global Application Configuration.
    </p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load the report: {{ error }}</p>
    <p v-else-if="items.length === 0">No same-person cases found.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Exception ID</th>
          <th>Account</th>
          <th>User</th>
          <th>Linked On</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="`${item.exceptionKey}-${item.occurredAt}`">
          <td>{{ item.exceptionID }}</td>
          <td>{{ item.accountName }}</td>
          <td>{{ item.userDisplayName }}</td>
          <td>{{ formatDate(item.occurredAt) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
</style>
