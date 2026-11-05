<script setup>
// D-345: the hard-coded credentials DNA (Discovery & Audit) found, from the latest DNA import: where each one is
// (machine, application, file, account, target) and DNA's masked line from the file. Only holders of
// ViewDnaSensitiveColumns open it. Paged on the server; searched, sorted (click a heading; shift-click adds one) and
// exported as the page shows it.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'

const { t } = useI18n()
const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

const rows = ref([])
const search = ref('')
const page = ref(1)
const loading = ref(true)
const error = ref(null)
const sortColumns = ref([])
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))

// Keys written out (D-227).
const columns = [
  { field: 'MachineName', labelKey: 'reports.dnaHardCoded.machine' },
  { field: 'ApplicationName', labelKey: 'reports.dnaHardCoded.application' },
  { field: 'AccountName', labelKey: 'reports.dnaHardCoded.account' },
  { field: 'HardCodedInFile', labelKey: 'reports.dnaHardCoded.file' },
  { field: null, labelKey: 'reports.dnaHardCoded.where' },
  { field: 'PasswordLength', labelKey: 'reports.dnaHardCoded.passwordLength' },
  { field: 'TargetSystemAddress', labelKey: 'reports.dnaHardCoded.target' }
]

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

function ariaSortFor(field) {
  if (!field || sortColumns.value.length === 0 || sortColumns.value[0].field !== field) return 'none'
  return sortColumns.value[0].descending ? 'descending' : 'ascending'
}

function toggleSort(field, event) {
  const existing = sortColumns.value.findIndex(s => s.field === field)
  if (!event.shiftKey) {
    sortColumns.value = existing === 0 && sortColumns.value.length === 1
      ? [{ field, descending: !sortColumns.value[0].descending }]
      : [{ field, descending: false }]
  } else if (existing === -1) {
    sortColumns.value = [...sortColumns.value, { field, descending: false }]
  } else {
    const updated = [...sortColumns.value]
    updated[existing] = { ...updated[existing], descending: !updated[existing].descending }
    sortColumns.value = updated
  }
  page.value = 1
  load()
}

const sortQuery = computed(() => sortColumns.value.map(s => `${s.field}:${s.descending ? 'desc' : 'asc'}`).join(','))
const exportParams = computed(() => ({ search: search.value.trim() || undefined, sort: sortQuery.value || undefined }))

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSizeStore.current) })
    if (search.value.trim()) params.set('search', search.value.trim())
    if (sortQuery.value) params.set('sort', sortQuery.value)
    const response = await fetch(`/api/reports/dna-hard-coded-credentials?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    readTotalCount(response)
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

function applySearch() {
  page.value = 1
  load()
}

function onPageChange(newPage) {
  page.value = newPage
  load()
}

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('reports.dnaHardCoded.title') }}</h2>
    <p class="hint">{{ t('reports.dnaHardCoded.hint') }}</p>
    <form class="filter-row" @submit.prevent="applySearch">
      <label class="field-label"><span class="field-label-text">{{ t('reports.dnaHardCoded.search') }}</span>
        <input v-model="search" type="search" /></label>
      <button type="submit">{{ t('reports.dnaHardCoded.apply') }}</button>
    </form>
    <ReportExportButtons path="/api/reports/dna-hard-coded-credentials/export" :params="exportParams" />
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="applySearch" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.dnaHardCoded.loadFailed', { error }) }}</p>
    <table v-else-if="rows.length">
      <thead>
        <tr>
          <th v-for="col in columns" :key="col.labelKey" :aria-sort="ariaSortFor(col.field)">
            <button v-if="col.field" type="button" class="sort-button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
            <template v-else>{{ t(col.labelKey) }}</template>
          </th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.dnaHardCodedKey">
          <td>{{ row.machineName }}<template v-if="row.machineType || row.osVersion"><br /><small>{{ [row.machineType, row.osVersion].filter(Boolean).join(' · ') }}</small></template></td>
          <td>{{ row.applicationName }}<template v-if="row.applicationServer || row.siteName"><br /><small>{{ [row.applicationServer, row.siteName].filter(Boolean).join(' · ') }}</small></template></td>
          <td>{{ row.accountName }}</td>
          <td class="file">{{ row.hardCodedInFile }}</td>
          <td class="where"><code>{{ row.credentialLocation }}</code></td>
          <td>{{ row.passwordLength ?? '' }}</td>
          <td>{{ row.targetSystemAddress }}<template v-if="row.targetSystemType"><br /><small>{{ row.targetSystemType }}</small></template></td>
        </tr>
      </tbody>
    </table>
    <EmptyListMessage :show="!loading && !error && rows.length === 0" :message="totalCount === 0 ? t('reports.dnaHardCoded.emptyNone') : t('reports.dnaHardCoded.emptyFiltered')" />
  </div>
</template>

<style scoped>
.sort-button {
  background: none;
  border: none;
  padding: 0;
  font: inherit;
  font-weight: bold;
  color: inherit;
  cursor: pointer;
}
.file, .where {
  overflow-wrap: anywhere;
  max-width: 28rem;
}
</style>
