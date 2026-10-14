<script setup>
// Calls GET /api/reports/overdue-at-risk (ReportsController) -- accounts
// past TargetRemediationDate that aren't yet complete (D-56).
import { ref, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const accounts = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/overdue-at-risk')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
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
    <h2>{{ t('reports.overdueAtRiskWorklist.title') }}</h2>
    <ReportExportButtons path="/api/reports/overdue-at-risk/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.overdueAtRiskWorklist.loadFailed', { error }) }}</p>
    <p v-else-if="accounts.length === 0">{{ t('reports.overdueAtRiskWorklist.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.overdueAtRiskWorklist.colAccount') }}</th>
          <th>{{ t('reports.overdueAtRiskWorklist.colStage') }}</th>
          <th>{{ t('reports.overdueAtRiskWorklist.colStatus') }}</th>
          <th>{{ t('reports.overdueAtRiskWorklist.colRiskLevel') }}</th>
          <th>{{ t('reports.overdueAtRiskWorklist.colOwner') }}</th>
          <th>{{ t('reports.overdueAtRiskWorklist.colTargetDate') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="account in accounts" :key="account.accountKey">
          <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.accountName }}</router-link></td>
          <td>{{ account.stageName }}</td>
          <td>{{ account.statusName }}</td>
          <td>{{ account.riskLevelName }}</td>
          <td>{{ account.ownerName }}</td>
          <td>{{ formatDate(account.targetRemediationDate) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
