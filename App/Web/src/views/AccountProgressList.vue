<script setup>
// Calls the real GET /api/account-progress endpoint (App/Api/Controllers/AccountProgressController.cs)
// to demonstrate the front-to-back pattern end to end. D-42's multi-layer
// sort/filter isn't implemented here yet -- this is a plain list.
import { ref, onMounted } from 'vue'

const accounts = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/account-progress')
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
    <h1>Account Progress</h1>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">Could not load accounts: {{ error }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>Account</th>
          <th>Stage</th>
          <th>Status</th>
          <th>Risk Level</th>
          <th>Owner</th>
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
        </tr>
      </tbody>
    </table>
  </div>
</template>
