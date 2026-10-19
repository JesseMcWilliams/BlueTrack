<script setup>
// Calls GET /api/reports/stage-status-summary (ReportsController) -- an
// account count per (Stage, Status) cell (D-56), rendered as one row per
// stage with a column per status.
import { ref, computed, onMounted } from 'vue'
import { formatCount } from '../../utils/formatCount'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const rows = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/stage-status-summary')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
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
    <h2>{{ t('reports.stageStatusFunnelSummary.title') }}</h2>
    <ReportExportButtons path="/api/reports/stage-status-summary/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.stageStatusFunnelSummary.loadFailed', { error }) }}</p>
    <p v-else-if="rows.length === 0">{{ t('reports.stageStatusFunnelSummary.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.stageStatusFunnelSummary.colStage') }}</th>
          <th v-for="status in statuses" :key="status">{{ status }}</th>
          <th>{{ t('reports.stageStatusFunnelSummary.colTotal') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="stage in stages" :key="stage">
          <td>{{ stage }}</td>
          <!-- UI review W2-1/W7-1: a count opens the accounts behind it (the Accounts list's default view, which hides deleted
               accounts, as this report does). A zero has nothing behind it. -->
          <td v-for="status in statuses" :key="status">
            <router-link v-if="countFor(stage, status) > 0" :to="{ name: 'account-progress-list', query: { stage, status } }"
                         :aria-label="t('reports.stageStatusFunnelSummary.showCell', { count: formatCount(countFor(stage, status)), stage, status })">{{ formatCount(countFor(stage, status)) }}</router-link>
            <template v-else>{{ formatCount(countFor(stage, status)) }}</template>
          </td>
          <td>
            <router-link v-if="stageTotal(stage) > 0" :to="{ name: 'account-progress-list', query: { stage } }"
                         :aria-label="t('reports.stageStatusFunnelSummary.showStage', { count: formatCount(stageTotal(stage)), stage })">{{ formatCount(stageTotal(stage)) }}</router-link>
            <template v-else>{{ formatCount(stageTotal(stage)) }}</template>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
