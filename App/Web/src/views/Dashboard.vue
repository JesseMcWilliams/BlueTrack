<script setup>
// D-99: analyst-facing home page (D-22) -- built from data already served
// by the Reports/Risk Exceptions pages, not new backend rollups, so this
// stays a lightweight "at a glance" summary rather than a duplicate report.
import { ref, onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'

const rights = useRightsStore()

const stageFunnel = ref([])
const overdueAtRiskCount = ref(null)
const overdueReviewCount = ref(null)
const activeExceptionsCount = ref(null)
const errors = ref({})
const loading = ref(true)

async function loadCount(key, url) {
  try {
    const response = await fetch(url)
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    return (await response.json()).length
  } catch (err) {
    errors.value[key] = err.message
    return null
  }
}

async function loadStageFunnel() {
  try {
    const response = await fetch('/api/reports/stage-status-summary')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    const rows = await response.json()
    const totals = new Map()
    for (const row of rows) {
      const existing = totals.get(row.stageName) ?? { stageOrder: row.stageOrder, stageName: row.stageName, accountCount: 0 }
      existing.accountCount += row.accountCount
      totals.set(row.stageName, existing)
    }
    stageFunnel.value = [...totals.values()].sort((a, b) => a.stageOrder - b.stageOrder)
  } catch (err) {
    errors.value.stageFunnel = err.message
  }
}

async function load() {
  loading.value = true
  await rights.ensureLoaded()

  const tasks = [
    loadStageFunnel(),
    loadCount('overdueAtRisk', '/api/reports/overdue-at-risk').then(count => { overdueAtRiskCount.value = count }),
    loadCount('overdueReview', '/api/risk-exceptions/overdue-review').then(count => { overdueReviewCount.value = count })
  ]

  // ApproveExceptions-gated (RiskExceptionsController) -- most roles don't
  // hold it (D-94), so this card is skipped rather than attempted and 403'd.
  if (rights.hasPermission('ApproveExceptions')) {
    tasks.push(loadCount('activeExceptions', '/api/risk-exceptions/active').then(count => { activeExceptionsCount.value = count }))
  }

  await Promise.all(tasks)
  loading.value = false
}

onMounted(load)
</script>

<template>
  <div>
    <h1>Dashboard</h1>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <section>
        <h2>Accounts by Stage</h2>
        <p v-if="errors.stageFunnel" role="alert">{{ errors.stageFunnel }}</p>
        <table v-else>
          <thead>
            <tr><th>Stage</th><th>Accounts</th></tr>
          </thead>
          <tbody>
            <tr v-for="stage in stageFunnel" :key="stage.stageName">
              <td>{{ stage.stageName }}</td>
              <td>{{ stage.accountCount }}</td>
            </tr>
          </tbody>
        </table>
        <p><router-link :to="{ name: 'reports-stage-status-summary' }">View full stage/status breakdown</router-link></p>
      </section>

      <section>
        <h2>Overdue / At-Risk Accounts</h2>
        <p v-if="errors.overdueAtRisk" role="alert">{{ errors.overdueAtRisk }}</p>
        <p v-else>
          {{ overdueAtRiskCount }} account(s) overdue or at risk.
          <router-link :to="{ name: 'reports-overdue-worklist' }">View worklist</router-link>
        </p>
      </section>

      <section>
        <h2>Risk Exceptions Needing Attention</h2>
        <p v-if="errors.overdueReview" role="alert">{{ errors.overdueReview }}</p>
        <p v-else>
          {{ overdueReviewCount }} exception(s) past their review date.
          <router-link :to="{ name: 'risk-exceptions-overdue-worklist' }">View worklist</router-link>
        </p>

        <template v-if="rights.hasPermission('ApproveExceptions')">
          <p v-if="errors.activeExceptions" role="alert">{{ errors.activeExceptions }}</p>
          <p v-else>
            {{ activeExceptionsCount }} active exception(s) awaiting approval.
            <router-link :to="{ name: 'risk-exceptions-approval-worklist' }">View worklist</router-link>
          </p>
        </template>
      </section>
    </template>
  </div>
</template>
