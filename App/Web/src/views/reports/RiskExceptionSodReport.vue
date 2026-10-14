<script setup>
// Calls GET /api/reports/risk-exception-sod (ReportsController), gated by
// ViewRiskExceptionSodReport. Detective/audit report (Option C): every
// historical case of the same user both approving a Risk Exception and
// linking it to an account, built entirely from the existing audit trail --
// reported regardless of whether the enforcement toggle on the Global
// Application Configuration page is (or ever was) on.
import { ref, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/risk-exception-sod')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
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
    <h2>{{ t('reports.riskExceptionSodReport.title') }}</h2>
    <p class="hint">{{ t('reports.riskExceptionSodReport.hint') }}</p>
    <ReportExportButtons path="/api/reports/risk-exception-sod/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.riskExceptionSodReport.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.riskExceptionSodReport.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.riskExceptionSodReport.colExceptionId') }}</th>
          <th>{{ t('reports.riskExceptionSodReport.colAccount') }}</th>
          <th>{{ t('reports.riskExceptionSodReport.colUser') }}</th>
          <th>{{ t('reports.riskExceptionSodReport.colLinkedOn') }}</th>
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
