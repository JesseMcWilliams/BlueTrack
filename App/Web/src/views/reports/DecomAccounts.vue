<script setup>
// D-186: GET /api/reports/decom-accounts (web.vw_decom_account) --
// accounts in a safe flagged for deletion, or whose own name matches the
// account decommission pattern. An account not yet deleted gets a note; if
// the same account (username + address) is also in another, unflagged
// safe, the note names it -- it has probably moved there. Read-only, no
// permission gate.
import { ref, onMounted, watch } from 'vue'
import { useClientTable } from '../../composables/useClientTable'
import { useAddressState } from '../../composables/useAddressState'
import { usePageSizeStore } from '../../stores/pageSize'
import { readText, readPage, compactQuery } from '../../utils/listQuery'
import { formatCount } from '../../utils/formatCount'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const error = ref(null)
const loading = ref(true)
const hideDeleted = ref(false)

// UI review W6-1 and W6-2: search, sort, count and paging in the page, and a Safe filter (the Safes report links here
// with ?safe=NAME to show the accounts in one flagged Safe).
const pageSizeStore = usePageSizeStore()
const safeFilter = ref('')
const table = useClientTable({
  rows: items,
  columns: {
    userName: i => i.userName || i.accountName,
    address: i => i.address,
    accountName: i => i.accountName,
    safeName: i => i.safeName,
    sourceSystemName: i => i.sourceSystemName,
    isDeleted: i => (i.isDeleted ? 1 : 0)
  },
  searchText: i => [i.userName, i.accountName, i.address, i.safeName, i.sourceSystemName].filter(Boolean).join(' '),
  extraFilter: i => (!hideDeleted.value || !i.isDeleted) && (!safeFilter.value || i.safeName === safeFilter.value),
  pageSize: () => pageSizeStore.current
})

const { isApplying } = useAddressState({
  sources: () => [table.search.value, safeFilter.value, hideDeleted.value, table.sortText.value, table.page.value],
  apply(query) {
    table.search.value = readText(query, 'q')
    safeFilter.value = readText(query, 'safe')
    hideDeleted.value = readText(query, 'notDeleted') === '1'
    table.applySortText(readText(query, 'sort'))
    table.page.value = readPage(query)
  },
  current: () => compactQuery({
    q: table.search.value.trim(), safe: safeFilter.value, notDeleted: hideDeleted.value ? '1' : '',
    sort: table.sortText.value, page: table.page.value
  }, { page: 1 })
})
// A change to the search or a filter starts over at page 1 (not when the address itself changed them).
watch([table.search, safeFilter, hideDeleted], () => {
  if (!isApplying()) table.page.value = 1
})

const columnList = [
  { field: 'userName', label: 'reports.decomAccounts.colUsername' },
  { field: 'address', label: 'reports.decomAccounts.colAddress' },
  { field: 'accountName', label: 'reports.decomAccounts.colAccountName' },
  { field: 'safeName', label: 'reports.decomAccounts.colSafe' },
  { field: 'sourceSystemName', label: 'reports.decomAccounts.colSource' }
]

function why(item) {
  const reasons = []
  if (item.inFlaggedSafe) reasons.push(t('reports.decomAccounts.safeFlagged'))
  if (item.nameFlagged) reasons.push(t('reports.decomAccounts.nameFlagged'))
  return reasons.join(', ')
}

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/decom-accounts')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
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
    <h2>{{ t('reports.decomAccounts.title') }}</h2>
    <i18n-t keypath="reports.decomAccounts.hint" tag="p" class="hint" scope="global">
      <template #safeLink><router-link :to="{ name: 'reports-decom-safes' }">{{ t('reports.decomAccounts.hintSafeLink') }}</router-link></template>
      <template #pattern><strong>{{ t('reports.decomAccounts.hintPattern') }}</strong></template>
      <template #notDeleted><strong>{{ t('reports.decomAccounts.hintNotDeleted') }}</strong></template>
      <template #alsoIn><strong>{{ t('reports.decomAccounts.hintAlsoIn') }}</strong></template>
    </i18n-t>
    <p class="hint">{{ t('reports.decomAccounts.whatNext') }}</p>
    <ReportExportButtons path="/api/reports/decom-accounts/export" :params="{ notDeletedOnly: hideDeleted }" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.decomAccounts.loadFailed', { error }) }}</p>
    <template v-else>
      <p class="filter-row">
        <label class="field-label"><span class="field-label-text">{{ t('reports.decomAccounts.search') }}</span> <input v-model="table.search.value" type="search" /></label>
        <label><input v-model="hideDeleted" type="checkbox" /> {{ t('reports.decomAccounts.onlyNotDeleted') }}</label>
        <span v-if="safeFilter" class="safe-filter">{{ t('reports.decomAccounts.safeFilter', { safe: safeFilter }) }} <button type="button" @click="safeFilter = ''">{{ t('reports.decomAccounts.clearSafe') }}</button></span>
      </p>
      <FilterCountSummary :shown="table.pageRows.value.length" :filtered-count="table.filteredCount.value" :total="items.length" :page="table.page.value" :page-size="pageSizeStore.current" />
      <Pager :page="table.page.value" :page-count="table.pageCount.value" @update:page="table.page.value = $event" @page-size-change="table.page.value = 1" />
      <p v-if="table.filteredCount.value === 0">{{ items.length === 0 ? t('reports.decomAccounts.empty') : t('reports.decomAccounts.emptyFiltered') }}</p>
      <table v-else>
        <thead>
          <tr>
            <th v-for="col in columnList" :key="col.field" :aria-sort="table.ariaSort(col.field)">
              <button type="button" @click="table.toggleSort(col.field)">{{ t(col.label) }} <span aria-hidden="true">{{ table.indicator(col.field) }}</span></button>
            </th>
            <th>{{ t('reports.decomAccounts.colFlaggedBy') }}</th>
            <th :aria-sort="table.ariaSort('isDeleted')"><button type="button" @click="table.toggleSort('isDeleted')">{{ t('reports.decomAccounts.colNote') }} <span aria-hidden="true">{{ table.indicator('isDeleted') }}</span></button></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in table.pageRows.value" :key="item.accountKey">
            <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: item.accountKey } }">{{ item.userName || item.accountName }}</router-link><span v-if="!item.userName" class="no-username">{{ ' ' + t('reports.decomAccounts.noUsername') }}</span></td>
            <td>{{ item.address }}</td>
            <td>{{ item.accountName }}</td>
            <td>{{ item.safeName }}</td>
            <td>{{ item.sourceSystemName }}</td>
            <td>{{ why(item) }}</td>
            <td>
              <template v-if="item.isDeleted">{{ t('reports.decomAccounts.deleted') }}</template>
              <template v-else><strong>{{ t('reports.decomAccounts.notDeletedYet') }}</strong></template>
              <template v-if="item.otherSafes">{{ ' ' + t('reports.decomAccounts.alsoIn', { safes: item.otherSafes }) }}</template>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.no-username {
  font-size: 0.85em;
  opacity: 0.8;
}
</style>
