<script setup>
// D-212 (Data Sources phase 4c): accounts from another system (an account
// inventory data feed) that may be a CyberArk account but didn't match it
// exactly -- same user name, with addresses that agree by short host name or
// by DNS. Link confirms the pair, as an exact match would (the feed account is
// hidden and moves to Onboarded to Vault); Dismiss means they're different
// accounts, and the pair isn't proposed again. Gated by ConfirmReconciliation.
import { ref, computed, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import { confirmDelete } from '../../composables/useConfirmDialog'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

const page = ref(1)
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))
const rows = ref([])
const error = ref(null)
const actionError = ref(null)
const loading = ref(true)
const busyKey = ref(null)

const methodLabelKeys = {
  FeedShortName: 'reports.possibleAccountMatches.methodShortName',
  FeedDnsForward: 'reports.possibleAccountMatches.methodDnsForward',
  FeedDnsReverse: 'reports.possibleAccountMatches.methodDnsReverse'
}

function onPageChange(newPage) {
  page.value = newPage
  load()
}

function onPageSizeChange() {
  page.value = 1
  load()
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams({ page: page.value, pageSize: pageSizeStore.current })
    const response = await fetch(`/api/reports/possible-matches?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    readTotalCount(response)
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

async function act(row, action) {
  if (action === 'link' && !(await confirmDelete(
    t('reports.possibleAccountMatches.linkConfirm', { user: row.feedUserName, address: row.feedAddress, source: row.feedSourceName, account: row.cyberArkAccountName }),
    t('reports.possibleAccountMatches.link')
  ))) return

  actionError.value = null
  busyKey.value = row.reconciliationKey
  try {
    const response = await fetch(`/api/reports/possible-matches/${row.reconciliationKey}/${action}`, { method: 'POST' })
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail ?? t('common.requestFailed', { status: response.status }))
    await load()
  } catch (err) {
    actionError.value = err.message
  } finally {
    busyKey.value = null
  }
}

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('reports.possibleAccountMatches.title') }}</h2>
    <i18n-t keypath="reports.possibleAccountMatches.hint" tag="p" class="hint" scope="global">
      <template #link><strong>{{ t('reports.possibleAccountMatches.link') }}</strong></template>
      <template #dismiss><strong>{{ t('reports.possibleAccountMatches.dismiss') }}</strong></template>
    </i18n-t>
    <ReportExportButtons path="/api/reports/possible-matches/export" />
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="actionError" role="alert">{{ actionError }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.possibleAccountMatches.loadFailed', { error }) }}</p>
    <p v-else-if="rows.length === 0">{{ t('reports.possibleAccountMatches.empty') }}</p>
    <table v-else>
      <thead>
        <tr><th>{{ t('reports.possibleAccountMatches.colOtherAccount') }}</th><th>{{ t('reports.possibleAccountMatches.colCyberArkAccount') }}</th><th>{{ t('reports.possibleAccountMatches.colWhy') }}</th><th>{{ t('reports.possibleAccountMatches.colFound') }}</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.reconciliationKey">
          <td>
            <router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.feedAccountKey } }">{{ row.feedUserName }}</router-link>{{ ' ' + t('reports.possibleAccountMatches.onAddress', { address: row.feedAddress }) }}<br /><small>{{ row.feedSourceName }}</small>
          </td>
          <td>
            <router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.cyberArkAccountKey } }">{{ row.cyberArkAccountName }}</router-link>
            <br /><small>{{ t('reports.possibleAccountMatches.userOnAddress', { user: row.cyberArkUserName, address: row.cyberArkAddress, source: row.cyberArkSourceName }) }}</small>
          </td>
          <td>{{ methodLabelKeys[row.matchMethod] ? t(methodLabelKeys[row.matchMethod]) : row.matchMethod }}<br /><small>{{ row.reason }}</small></td>
          <td>{{ formatDate(row.matchedDate) }}</td>
          <td>
            <button type="button" :disabled="busyKey === row.reconciliationKey" @click="act(row, 'link')">{{ t('reports.possibleAccountMatches.link') }}</button>
            <button type="button" :disabled="busyKey === row.reconciliationKey" @click="act(row, 'dismiss')">{{ t('reports.possibleAccountMatches.dismiss') }}</button>
          </td>
        </tr>
      </tbody>
    </table>
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
</style>
