<script setup>
// D-288: Reports > CPM Health. GET /api/reports/cpm-health (ViewDashboard): the accounts whose CPM status is
// failure (the CPM couldn't verify, change or reconcile them at its last try), longest failing first, with what the
// CPM last did. The CpmHealth notification emails the newly failing ones each day.
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatDate } from '../../utils/formatDate'
import ReportExportButtons from '../../components/ReportExportButtons.vue'

const { t } = useI18n()
const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/cpm-health')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})

const managed = row => row.autoManaged == null ? '' : (row.autoManaged ? t('common.yes') : t('common.no')) + (row.manualReason ? ` (${row.manualReason})` : '')
</script>

<template>
  <div>
    <h2>{{ t('reports.cpmHealth.title') }}</h2>
    <p class="hint">{{ t('reports.cpmHealth.hint') }}</p>
    <ReportExportButtons path="/api/reports/cpm-health/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.cpmHealth.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.cpmHealth.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.cpmHealth.colAccount') }}</th>
          <th>{{ t('reports.cpmHealth.colSafe') }}</th>
          <th>{{ t('reports.cpmHealth.colPlatform') }}</th>
          <th>{{ t('reports.cpmHealth.colSource') }}</th>
          <th>{{ t('reports.cpmHealth.colSince') }}</th>
          <th>{{ t('reports.cpmHealth.colLastAction') }}</th>
          <th>{{ t('reports.cpmHealth.colLastVerified') }}</th>
          <th>{{ t('reports.cpmHealth.colLastChanged') }}</th>
          <th>{{ t('reports.cpmHealth.colManaged') }}</th>
          <th>{{ t('reports.cpmHealth.colOwner') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in items" :key="row.accountKey">
          <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.accountKey } }">{{ row.accountName }}</router-link></td>
          <td>{{ row.safeName ?? '' }}</td>
          <td>{{ row.platformId ?? '' }}</td>
          <td>{{ row.sourceSystemName ?? '' }}</td>
          <td>{{ formatDate(row.cpmFailureSince) }}</td>
          <td>{{ row.lastCPMAction ? t('reports.cpmHealth.action', { action: row.lastCPMAction, result: row.lastCPMActionResult ?? '—' }) : '' }}</td>
          <td>{{ formatDate(row.lastVerifiedDate) }}</td>
          <td>{{ formatDate(row.lastCPMModifiedDate) }}</td>
          <td>{{ managed(row) }}</td>
          <td>{{ row.ownerName ?? '' }}</td>
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
