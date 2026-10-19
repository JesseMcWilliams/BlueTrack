<script setup>
// Calls GET /api/reports/overdue-at-risk (ReportsController) -- accounts
// past TargetRemediationDate that aren't yet complete (D-56).
import { ref, onMounted, watch } from 'vue'
import { useClientTable } from '../../composables/useClientTable'
import { useAddressState } from '../../composables/useAddressState'
import { usePageSizeStore } from '../../stores/pageSize'
import { readText, readPage, compactQuery } from '../../utils/listQuery'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import { formatDate } from '../../utils/formatDate'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const accounts = ref([])
const error = ref(null)
const loading = ref(true)

// UI review W7-4: search, sort, count and paging in the page (the API sends every overdue account), and how many days
// past its target date each account is.
const pageSizeStore = usePageSizeStore()
function daysOverdue(account) {
  if (!account.targetRemediationDate) return null
  const target = new Date(account.targetRemediationDate)
  if (Number.isNaN(target.getTime())) return null
  return Math.max(0, Math.floor((Date.now() - target.getTime()) / 86400000))
}
const table = useClientTable({
  rows: accounts,
  columns: {
    accountName: a => a.accountName,
    stageName: a => a.stageName,
    statusName: a => a.statusName,
    riskLevelName: a => a.riskLevelName,
    ownerName: a => a.ownerName,
    targetRemediationDate: a => a.targetRemediationDate,
    daysOverdue: a => daysOverdue(a)
  },
  searchText: a => [a.accountName, a.stageName, a.statusName, a.riskLevelName, a.ownerName].filter(Boolean).join(' '),
  pageSize: () => pageSizeStore.current
})
const { isApplying } = useAddressState({
  sources: () => [table.search.value, table.sortText.value, table.page.value],
  apply(query) {
    table.search.value = readText(query, 'q')
    table.applySortText(readText(query, 'sort'))
    table.page.value = readPage(query)
  },
  current: () => compactQuery({ q: table.search.value.trim(), sort: table.sortText.value, page: table.page.value }, { page: 1 })
})
watch(table.search, () => {
  if (!isApplying()) table.page.value = 1
})
const columnList = [
  { field: 'accountName', label: 'reports.overdueAtRiskWorklist.colAccount' },
  { field: 'stageName', label: 'reports.overdueAtRiskWorklist.colStage' },
  { field: 'statusName', label: 'reports.overdueAtRiskWorklist.colStatus' },
  { field: 'riskLevelName', label: 'reports.overdueAtRiskWorklist.colRiskLevel' },
  { field: 'ownerName', label: 'reports.overdueAtRiskWorklist.colOwner' },
  { field: 'targetRemediationDate', label: 'reports.overdueAtRiskWorklist.colTargetDate' },
  { field: 'daysOverdue', label: 'reports.overdueAtRiskWorklist.colDaysOverdue' }
]

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
    <template v-else>
    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">{{ t('reports.overdueAtRiskWorklist.search') }}</span> <input v-model="table.search.value" type="search" /></label>
    </p>
    <FilterCountSummary :shown="table.pageRows.value.length" :filtered-count="table.filteredCount.value" :total="accounts.length" :page="table.page.value" :page-size="pageSizeStore.current" />
    <Pager :page="table.page.value" :page-count="table.pageCount.value" @update:page="table.page.value = $event" @page-size-change="table.page.value = 1" />
    <p v-if="table.filteredCount.value === 0">{{ t('reports.overdueAtRiskWorklist.emptyFiltered') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-for="col in columnList" :key="col.field" :aria-sort="table.ariaSort(col.field)">
            <button type="button" @click="table.toggleSort(col.field)">{{ t(col.label) }} <span aria-hidden="true">{{ table.indicator(col.field) }}</span></button>
          </th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="account in table.pageRows.value" :key="account.accountKey">
          <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.accountName }}</router-link></td>
          <td>{{ account.stageName }}</td>
          <td>{{ account.statusName }}</td>
          <td>{{ account.riskLevelName }}</td>
          <td>{{ account.ownerName }}</td>
          <td>{{ formatDate(account.targetRemediationDate) }}</td>
          <td>{{ daysOverdue(account) ?? '' }}</td>
        </tr>
      </tbody>
    </table>
    </template>
  </div>
</template>
