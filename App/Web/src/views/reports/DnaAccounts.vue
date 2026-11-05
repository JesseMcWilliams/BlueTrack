<script setup>
// D-325: the accounts of the latest DNA (Discovery & Audit) workbook imported on Admin > Data Sources, by finding
// (the workbook's sheet), with whether BlueTrack tracks each one. Paged on the server; filtered by finding, tracked
// or not, and a search of account, machine and display name.
// D-341: click a heading to sort (shift-click adds a column). D-342: with ManageDiscoveredAccounts, tick accounts and
// Track in BlueTrack makes each a BlueTrack account at the first stage. D-344: columns an admin restricted come back
// blank for someone without ViewDnaSensitiveColumns; the page says which.
import { ref, computed, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import { useRightsStore } from '../../stores/rights'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import { formatDate } from '../../utils/formatDate'
import { formatCount } from '../../utils/formatCount'
import { byLabel } from '../../utils/sortOptions'
import { problemMessage } from '../../utils/problemText'

const { t } = useI18n()
const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()
const rights = useRightsStore()

const rows = ref([])
const sheets = ref([])
// The API lists sheets in workbook (import) order; the filter is alphabetical.
const sortedSheets = computed(() => byLabel(sheets.value))
const sheet = ref('')
const tracked = ref('')
const unresolvedOnly = ref(false)   // D-347
const search = ref('')
const page = ref(1)
const loading = ref(true)
const error = ref(null)
const notice = ref(null)
const hiddenColumns = ref([])
const selected = ref(new Set())
const tracking = ref(false)
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))
const canTrack = computed(() => rights.hasPermission('ManageDiscoveredAccounts'))

// The sortable headings: field (as the API's sort takes it) and label. Keys written out (D-227).
const columns = [
  { field: 'SheetCode', labelKey: 'reports.dnaAccounts.finding' },
  { field: 'MachineName', labelKey: 'reports.dnaAccounts.machine' },
  { field: 'AccountName', labelKey: 'reports.dnaAccounts.account' },
  { field: 'AccountCategory', labelKey: 'reports.dnaAccounts.category' },
  { field: 'AccountGroup', labelKey: 'reports.dnaAccounts.group' },
  { field: 'AccountState', labelKey: 'reports.dnaAccounts.state' },
  { field: 'PasswordAgeDays', labelKey: 'reports.dnaAccounts.passwordAge' },
  { field: 'LastLoginDate', labelKey: 'reports.dnaAccounts.lastLogin' },
  { field: 'Tracked', labelKey: 'reports.dnaAccounts.inBlueTrack' }
]
const sortColumns = ref([])

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

function ariaSortFor(field) {
  if (sortColumns.value.length === 0 || sortColumns.value[0].field !== field) return 'none'
  return sortColumns.value[0].descending ? 'descending' : 'ascending'
}

function toggleSort(field, event) {
  const existing = sortColumns.value.findIndex(s => s.field === field)
  if (!event.shiftKey) {
    sortColumns.value = existing === 0 && sortColumns.value.length === 1
      ? [{ field, descending: !sortColumns.value[0].descending }]
      : [{ field, descending: ['PasswordAgeDays', 'LastLoginDate'].includes(field) }]
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
const isHidden = field => hiddenColumns.value.includes(field)

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams({ page: String(page.value), pageSize: String(pageSizeStore.current) })
    if (sheet.value) params.set('sheet', sheet.value)
    if (tracked.value) params.set('tracked', tracked.value)
    if (unresolvedOnly.value) params.set('unresolvedSid', 'true')
    if (search.value.trim()) params.set('search', search.value.trim())
    if (sortQuery.value) params.set('sort', sortQuery.value)
    const response = await fetch(`/api/reports/dna-accounts?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    readTotalCount(response)
    hiddenColumns.value = (response.headers?.get?.('X-Hidden-Columns') ?? '').split(',').filter(Boolean)
    rows.value = await response.json()
    selected.value = new Set()
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

const untrackedOnPage = computed(() => rows.value.filter(r => !r.trackedAccountKey))
const allSelected = computed(() => untrackedOnPage.value.length > 0 && untrackedOnPage.value.every(r => selected.value.has(r.dnaAccountKey)))

function toggleRow(row, on) {
  const next = new Set(selected.value)
  if (on) next.add(row.dnaAccountKey)
  else next.delete(row.dnaAccountKey)
  selected.value = next
}

function toggleAll(on) {
  selected.value = on ? new Set(untrackedOnPage.value.map(r => r.dnaAccountKey)) : new Set()
}

// D-342: each chosen account becomes a BlueTrack account (source DNA) at the first stage.
async function trackSelected() {
  tracking.value = true
  error.value = null
  notice.value = null
  try {
    const response = await fetch('/api/reports/dna-accounts/track', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dnaAccountKeys: [...selected.value] })
    })
    if (!response.ok) {
      error.value = await problemMessage(response, t('common.requestFailed', { status: response.status }))
      return
    }
    const result = await response.json()
    notice.value = t('reports.dnaAccounts.trackedMessage', { count: formatCount(result.tracked) }, result.tracked)
    await load()
  } finally {
    tracking.value = false
  }
}

watch([sheet, tracked, unresolvedOnly], applyFilters)

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
      <label class="checkbox-filter"><input v-model="unresolvedOnly" type="checkbox" /> {{ t('reports.dnaAccounts.unresolvedOnly') }}</label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.dnaAccounts.search') }}</span>
        <input v-model="search" type="search" /></label>
      <button type="submit">{{ t('reports.dnaAccounts.apply') }}</button>
    </form>
    <p v-if="hiddenColumns.length" class="hint hidden-note">{{ t('reports.dnaAccounts.hiddenNote', { count: formatCount(hiddenColumns.length) }, hiddenColumns.length) }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="canTrack && rows.length" class="track-row">
      <button type="button" :disabled="selected.size === 0 || tracking" @click="trackSelected">
        {{ t('reports.dnaAccounts.trackSelected', { count: formatCount(selected.size) }) }}
      </button>
      <small>{{ ' ' + t('reports.dnaAccounts.trackHint') }}</small>
    </p>
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="applyFilters" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.dnaAccounts.loadFailed', { error }) }}</p>
    <table v-else-if="rows.length">
      <thead>
        <tr>
          <th v-if="canTrack">
            <input type="checkbox" :checked="allSelected" :disabled="untrackedOnPage.length === 0" :aria-label="t('reports.dnaAccounts.selectAll')" @change="toggleAll($event.target.checked)" />
          </th>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button v-if="!isHidden(col.field)" type="button" class="sort-button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
            <template v-else>{{ t(col.labelKey) }}</template>
          </th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.dnaAccountKey">
          <td v-if="canTrack">
            <input v-if="!row.trackedAccountKey" type="checkbox" :checked="selected.has(row.dnaAccountKey)"
                   :aria-label="t('reports.dnaAccounts.selectRow', { account: row.accountName })" @change="toggleRow(row, $event.target.checked)" />
          </td>
          <td>{{ row.sheetCode }}</td>
          <td>
            {{ row.machineName }}<template v-if="row.instanceName"> / {{ row.instanceName }}</template><template v-if="row.databaseName"> / {{ row.databaseName }}</template>
            <template v-if="row.machineType || row.osVersion"><br /><small>{{ [row.machineType, row.osVersion].filter(Boolean).join(' · ') }}</small></template>
          </td>
          <td>
            {{ row.accountName }}<template v-if="row.displayName && row.displayName !== row.accountName"><br /><small>{{ row.displayName }}</small></template>
            <template v-if="row.accountDescription"><br /><small>{{ row.accountDescription }}</small></template>
            <template v-if="row.isUnresolvedSid"><br /><small class="unresolved-sid">{{ t('reports.dnaAccounts.unresolvedSid') }}</small></template>
          </td>
          <td>{{ row.accountCategory }}</td>
          <td>{{ row.privilegedDomainGroup || row.accountGroup || row.roles }}</td>
          <td>{{ row.accountState }}</td>
          <td>{{ row.passwordAgeDays === null || row.passwordAgeDays === undefined ? '' : t('reports.dnaAccounts.days', { count: formatCount(row.passwordAgeDays) }) }}</td>
          <td>{{ formatDate(row.lastLoginDate) }}</td>
          <td>
            <template v-if="row.trackedAccountKey">
              <router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.trackedAccountKey } }">{{ row.trackedAccountName }}</router-link>
              <template v-if="row.trackedBy"><br /><small>{{ t('reports.dnaAccounts.trackedBy', { by: row.trackedBy }) }}</small></template>
            </template>
            <template v-else>{{ t('reports.dnaAccounts.no') }}</template>
          </td>
        </tr>
      </tbody>
    </table>
    <EmptyListMessage :show="!loading && !error && rows.length === 0" :message="totalCount === 0 ? t('reports.dnaAccounts.emptyNone') : t('reports.dnaAccounts.emptyFiltered')" />
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
</style>
