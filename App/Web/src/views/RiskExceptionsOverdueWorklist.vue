<script setup>
// Calls GET /api/risk-exceptions/overdue-review (RiskExceptionsController)
// -- Active exceptions past ReviewDate (D-19). Distinct from the Reports
// area's Overdue/At-Risk Worklist, which is about account progress, not
// exceptions (Design_Application_Structure.md's own note on this).
import { ref, onMounted } from 'vue'

const exceptions = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/risk-exceptions/overdue-review')
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    exceptions.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>Overdue Exception Reviews</h2>
    <p>Active exceptions past their review date -- re-approve (extend the review date) or revoke.</p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load exceptions: {{ error }}</p>
    <p v-else-if="exceptions.length === 0">No exceptions are past their review date.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Exception ID</th>
          <th>Scope</th>
          <th>Justification</th>
          <th>Review Date</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="exception in exceptions" :key="exception.exceptionKey">
          <td><router-link :to="{ name: 'risk-exception-edit', params: { exceptionKey: exception.exceptionKey } }">{{ exception.exceptionID }}</router-link></td>
          <td>{{ exception.scopeType }}: {{ exception.scopeName }}</td>
          <td>{{ exception.justification }}</td>
          <td>{{ exception.reviewDate }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
