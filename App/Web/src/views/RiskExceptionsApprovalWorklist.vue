<script setup>
// Calls GET /api/risk-exceptions/active (RiskExceptionsController) -- every
// currently-Active exception. Requires ApproveExceptions (D-07); the API
// enforces this via a real authorization policy, but a 403 here still
// reads as a generic failure until the frontend has its own permission-
// aware routing/UI (this app doesn't check rights.permissionNames from
// /api/me before rendering yet -- a follow-up, same gap noted on the
// other permission-gated pages).
import { ref, onMounted } from 'vue'

const exceptions = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/risk-exceptions/active')
    if (!response.ok) {
      throw new Error(response.status === 403
        ? 'You do not have the ApproveExceptions permission.'
        : `Request failed: ${response.status}`)
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
    <h2>Exception Approval Worklist</h2>
    <p>Every currently-Active exception.</p>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">{{ error }}</p>
    <p v-else-if="exceptions.length === 0">No Active exceptions.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Exception ID</th>
          <th>Scope</th>
          <th>Justification</th>
          <th>Approved By</th>
          <th>Review Date</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="exception in exceptions"
          :key="exception.exceptionKey"
          @click="$router.push({ name: 'risk-exception-edit', params: { exceptionKey: exception.exceptionKey } })"
        >
          <td>{{ exception.exceptionID }}</td>
          <td>{{ exception.scopeType }}: {{ exception.scopeName }}</td>
          <td>{{ exception.justification }}</td>
          <td>{{ exception.approvedByName }}</td>
          <td>{{ exception.reviewDate }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
