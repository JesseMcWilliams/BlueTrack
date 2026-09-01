<script setup>
// Calls GET /api/reports/stage-status-summary (ReportsController) -- an
// account count per (Stage, Status) cell (D-56), rendered as one row per
// stage with a column per status.
import { ref, computed, onMounted } from 'vue'

const rows = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/stage-status-summary')
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})

// Pivot the flat (stage, status, count) rows into a stage x status grid.
const statuses = computed(() => [...new Set(rows.value.map(r => r.statusName))])
const stages = computed(() => {
  const byOrder = new Map()
  for (const row of rows.value) {
    if (!byOrder.has(row.stageOrder)) byOrder.set(row.stageOrder, row.stageName)
  }
  return [...byOrder.entries()].sort((a, b) => a[0] - b[0]).map(([, name]) => name)
})
function countFor(stageName, statusName) {
  return rows.value.find(r => r.stageName === stageName && r.statusName === statusName)?.accountCount ?? 0
}
function stageTotal(stageName) {
  return rows.value.filter(r => r.stageName === stageName).reduce((sum, r) => sum + r.accountCount, 0)
}
</script>

<template>
  <div>
    <h2>Stage/Status Funnel Summary</h2>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">Could not load summary: {{ error }}</p>
    <p v-else-if="rows.length === 0">No account progress data yet.</p>
    <table v-else>
      <thead>
        <tr>
          <th>Stage</th>
          <th v-for="status in statuses" :key="status">{{ status }}</th>
          <th>Total</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="stage in stages" :key="stage">
          <td>{{ stage }}</td>
          <td v-for="status in statuses" :key="status">{{ countFor(stage, status) }}</td>
          <td>{{ stageTotal(stage) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
