<script setup>
// Calls GET /api/risk-exceptions (RiskExceptionsController) -- every
// exception regardless of status, with an optional status filter.
import { ref, onMounted, watch } from 'vue'

const exceptions = ref([])
const error = ref(null)
const loading = ref(true)
const statusFilter = ref('')

async function load() {
  loading.value = true
  error.value = null
  try {
    const query = statusFilter.value ? `?status=${encodeURIComponent(statusFilter.value)}` : ''
    const response = await fetch(`/api/risk-exceptions${query}`)
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    exceptions.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(statusFilter, load)
</script>

<template>
  <div>
    <h1>Risk Exceptions</h1>
    <p>
      <router-link :to="{ name: 'risk-exception-create' }">+ New Exception</router-link>
    </p>
    <p>
      Status:
      <select v-model="statusFilter">
        <option value="">All</option>
        <option value="Active">Active</option>
        <option value="Expired">Expired</option>
        <option value="Revoked">Revoked</option>
      </select>
    </p>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">Could not load exceptions: {{ error }}</p>
    <p v-else-if="exceptions.length === 0">No exceptions found.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Exception ID</th>
          <th>Scope</th>
          <th>Justification</th>
          <th>Approved By</th>
          <th>Approval Date</th>
          <th>Review Date</th>
          <th>Status</th>
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
          <td>{{ exception.approvalDate }}</td>
          <td>{{ exception.reviewDate }}</td>
          <td>{{ exception.statusName }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
