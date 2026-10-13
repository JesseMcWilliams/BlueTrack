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

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

const page = ref(1)
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))
const rows = ref([])
const error = ref(null)
const actionError = ref(null)
const loading = ref(true)
const busyKey = ref(null)

const methodLabels = {
  FeedShortName: 'Same host name',
  FeedDnsForward: 'Same IP address (DNS)',
  FeedDnsReverse: 'Reverse DNS'
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
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
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
    `Link ${row.feedUserName} on ${row.feedAddress} (${row.feedSourceName}) to CyberArk account "${row.cyberArkAccountName}"? It will be hidden from Account Progress and counted as the CyberArk account.`,
    'Link'
  ))) return

  actionError.value = null
  busyKey.value = row.reconciliationKey
  try {
    const response = await fetch(`/api/reports/possible-matches/${row.reconciliationKey}/${action}`, { method: 'POST' })
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail ?? `Request failed: ${response.status}`)
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
    <h2>Possible Account Matches</h2>
    <p class="hint">
      Accounts from other systems (account inventory data feeds) that may be the same as a CyberArk account,
      but didn't match it exactly: the user name is the same, and the addresses agree by host name or DNS.
      <strong>Link</strong> treats them as one account, as an exact match would: the other system's account is hidden
      and moves to Onboarded to Vault. <strong>Dismiss</strong> means they're different accounts; the pair isn't suggested again.
    </p>
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="actionError" role="alert">{{ actionError }}</p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load report: {{ error }}</p>
    <p v-else-if="rows.length === 0">No possible matches to review.</p>
    <table v-else>
      <thead>
        <tr><th>Other system's account</th><th>CyberArk account</th><th>Why</th><th>Found</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="row in rows" :key="row.reconciliationKey">
          <td>
            <router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.feedAccountKey } }">{{ row.feedUserName }}</router-link>
            on {{ row.feedAddress }}<br /><small>{{ row.feedSourceName }}</small>
          </td>
          <td>
            <router-link :to="{ name: 'account-progress-detail', params: { accountKey: row.cyberArkAccountKey } }">{{ row.cyberArkAccountName }}</router-link>
            <br /><small>{{ row.cyberArkUserName }} on {{ row.cyberArkAddress }}, {{ row.cyberArkSourceName }}</small>
          </td>
          <td>{{ methodLabels[row.matchMethod] ?? row.matchMethod }}<br /><small>{{ row.reason }}</small></td>
          <td>{{ formatDate(row.matchedDate) }}</td>
          <td>
            <button type="button" :disabled="busyKey === row.reconciliationKey" @click="act(row, 'link')">Link</button>
            <button type="button" :disabled="busyKey === row.reconciliationKey" @click="act(row, 'dismiss')">Dismiss</button>
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
