<script setup>
// D-325: the accounts of the latest DNA (Discovery & Audit) workbook imported on Admin > Data Sources, by finding
// (the workbook's sheet), with whether BlueTrack tracks each one. Paged on the server; filtered by finding, tracked
// or not, and a search of account, machine and display name.
import { ref, computed, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import { formatDate } from '../../utils/formatDate'
import { formatCount } from '../../utils/formatCount'
import { byLabel } from '../../utils/sortOptions'

const { t } = useI18n()
const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

const rows = ref([])
const sheets = ref([])
// The API lists sheets in workbook (import) order; the filter is alphabetical.
const sortedSheets = computed(() => byLabel(sheets.value))
const sheet = ref('')
const tracked = ref('')
const search = ref('')
const page = ref(1)
const loading = ref(true)
const error = ref(null)
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSizeStore.current) })
    if (sheet.value) params.set('sheet', sheet.value)
    if (tracked.value) params.set('tracked', tracked.value)
    if (search.value.trim()) params.set('search', search.value.trim())
    const response = await fetch(`/api/reports/dna-accounts?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    readTotalCount(response)
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

function applyFilters() {
  page.value = 1
  load()
}

function onPageChange(newPage) {
  page.value = newPage
  load()
}

watch([sheet, tracked], applyFilters)

onMounted(async () => {
  const response = await fetch('/api/reports/dna-accounts/sheets')
  if (response.ok) sheets.value = await response.json()
  await load()
})
</script>

<template>
  <div>
    <h2>{{ t('reports.dnaAccounts.title') }}</h2>
    <p class="hint">{{ t('reports.dnaAccounts.hint') }}</p>
    <form class="filter-row" @submit.prevent="applyFilters">
      <label class="field-label"><span class="field-label-text">{{ t('reports.dnaAccounts.finding') }}</span>
        <select v-model="sheet">
          <option value="">{{ t('reports.dnaAccounts.allFindings') }}</option>
          <option v-for="s in sortedSheets" :key="s" :value="s">{{ s }}</option>
        </select></label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.dnaAccounts.trackedLabel') }}</span>
        <select v-model="tracked">
          <option value="">{{ t('reports.dnaAccounts.either') }}</option>
          <option value="false">{{ t('reports.dnaAccounts.notTracked') }}</option>
          <option value="true">{{ t('reports.dnaAccounts.tracked') }}</option>
        </select></label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.dnaAccounts.search') }}</span>
        <input v-model="search" type="search" /></label>
      <button type="submit">{{ t('reports.dnaAccounts.apply') }}</button>
    </form>
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="applyFilters" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.dnaAccounts.loadFailed', { error }) }}</p>
    <table v-else-if="rows.length">
      <thead>
        <tr>
          <th>{{ t('reports.dnaAccounts.finding') }}</th><th>{{ t('reports.dnaAccounts.machine') }}</th><th>{{ t('reports.dnaAccounts.account') }}</th>
          <th>{{ t('reports.dnaAccounts.category') }}</th><th>{{ t('reports.dnaAccounts.group') }}</th><th>{{ t('reports.dnaAccounts.state') }}</th>
          <th>{{ t('reports.dnaAccounts.passwordAge') }}</th><th>{{ t('reports.dnaAccounts.lastLogin') }}</th><th>{{ t('reports.dnaAccounts.inBlueTrack') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.dnaAccountKey">
          <td>{{ row.sheetCode }}</td>
          <td>{{ row.machineName }}<template v-if="row.instanceName"> / {{ row.instanceName }}</template><template v-if="row.databaseName"> / {{ row.databaseName }}</template></td>
          <td>{{ row.accountName }}<template v-if="row.displayName && row.displayName !== row.accountName"><br /><small>{{ row.displayName }}</small></template></td>
          <td>{{ row.accountCategory }}</td>
          <td>{{ row.privilegedDomainGroup || row.accountGroup }}</td>
          <td>{{ row.accountState }}</td>
          <td>{{ row.passwordAgeDays === null ? '' : t('reports.dnaAccounts.days', { count: formatCount(row.passwordAgeDays) }) }}</td>
          <td>{{ formatDate(row.lastLoginDate) }}</td>
          <td>
            <router-link v-if="row.trackedAccountKey" :to="{ name: 'account-progress-detail', params: { accountKey: row.trackedAccountKey } }">{{ row.trackedAccountName }}</router-link>
            <template v-else>{{ t('reports.dnaAccounts.no') }}</template>
          </td>
        </tr>
      </tbody>
    </table>
    <EmptyListMessage :show="!loading && !error && rows.length === 0" :message="totalCount === 0 ? t('reports.dnaAccounts.emptyNone') : t('reports.dnaAccounts.emptyFiltered')" />
  </div>
</template>
