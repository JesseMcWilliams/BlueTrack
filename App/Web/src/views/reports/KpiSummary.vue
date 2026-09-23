<script setup>
// Calls GET /api/reports/kpi-summary (ReportsController, D-143) -- five
// nested account counts (Total >= In Scope >= Onboarded >= Managed >=
// Compliant), rendered here as the four requested ratios. "In scope" means
// not currently excluded via an active Risk Accepted / Excluded exception
// (D-19/D-59); Onboarded/Managed are Blueprint stage thresholds; Compliant
// requires both the final stage and status Complete specifically.
import { ref, computed, onMounted } from 'vue'

const summary = ref(null)
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/kpi-summary')
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    summary.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})

function pct(numerator, denominator) {
  if (!denominator) return null
  return Math.round((numerator / denominator) * 1000) / 10
}

const kpis = computed(() => {
  if (!summary.value) return []
  const s = summary.value
  return [
    { label: 'Accounts In Scope vs. All Accounts', numerator: s.inScopeAccounts, denominator: s.totalAccounts },
    { label: 'Accounts Onboarded vs. Accounts In Scope', numerator: s.onboardedAccounts, denominator: s.inScopeAccounts },
    { label: 'Accounts Managed vs. Onboarded', numerator: s.managedAccounts, denominator: s.onboardedAccounts },
    { label: 'Accounts Managed vs. Compliance (% of Managed that are Compliant)', numerator: s.compliantAccounts, denominator: s.managedAccounts }
  ]
})
</script>

<template>
  <div>
    <h2>KPI Summary</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load KPI summary: {{ error }}</p>
    <template v-else>
      <table>
        <thead>
          <tr>
            <th>KPI</th>
            <th>Count</th>
            <th>Percentage</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="kpi in kpis" :key="kpi.label">
            <td>{{ kpi.label }}</td>
            <td>{{ kpi.numerator }} / {{ kpi.denominator }}</td>
            <td>{{ pct(kpi.numerator, kpi.denominator) === null ? 'N/A' : `${pct(kpi.numerator, kpi.denominator)}%` }}</td>
          </tr>
        </tbody>
      </table>
      <p>{{ summary.totalAccounts }} account(s) total.</p>
    </template>
  </div>
</template>
