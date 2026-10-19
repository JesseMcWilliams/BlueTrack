<script setup>
// D-42: multiple simultaneous filter layers (stage/status/riskLevel/owner,
// stacked with AND) plus multi-column sort -- click a column header to sort
// by just that column; shift-click another header to add it as a secondary
// sort key without losing the first (badges show the resulting priority).
import { ref, computed, watch, onMounted, nextTick } from 'vue'
import { formatDate } from '../utils/formatDate'
import { useTotalCount } from '../composables/useTotalCount'
import { useLatestRequest } from '../composables/useLatestRequest'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'
import { useRoute, useRouter } from 'vue-router'
import { useListStateStore } from '../stores/listState'
import { readText, readOneOf, readPage, parseSort, formatSort, compactQuery, sameQuery } from '../utils/listQuery'
import { useRightsStore } from '../stores/rights'
import { useAccountSelectionStore } from '../stores/accountSelection'
import { decomNote } from '../utils/decomNote'
import { formatCount } from '../utils/formatCount'
import SelectionBar from '../components/SelectionBar.vue'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../components/EmptyListMessage.vue'

const { t } = useI18n()

// D-182: bulk edit. "Select accounts" turns on a checkbox column; the
// selection (a store) survives paging, sorting and filtering. The bar
// itself is the shared SelectionBar (D-190).
const router = useRouter()
const route = useRoute()
const listState = useListStateStore()
const rights = useRightsStore()
const selection = useAccountSelectionStore()
const canEdit = computed(() => rights.hasPermission('EditAccountProgress'))
const canDelete = computed(() => rights.hasPermission('DeleteAccounts'))

// D-185: delete / undelete the selected accounts, with a required reason
// (an inline form in the selection bar, since the shared confirm dialog
// has no text box).
const deletion = ref(null) // { action: 'delete' | 'undelete', reason }
const deletionResult = ref(null)
const deletionError = ref(null)
const deletionSaving = ref(false)

function startDeletion(action) {
  deletion.value = { action, reason: '' }
  deletionResult.value = null
  deletionError.value = null
}

async function applyDeletion() {
  deletionError.value = null
  deletionSaving.value = true
  try {
    const response = await fetch(`/api/account-progress/${deletion.value.action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ accountKeys: selection.keys, reason: deletion.value.reason })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      deletionError.value = problem?.detail ?? t('pages.accountProgressList.failed', { status: response.status })
      return
    }
    const result = await response.json()
    deletionResult.value = { ...result, action: deletion.value.action }
    deletion.value = null
    // Keep only the skipped accounts selected, so they can be reviewed.
    selection.clear()
    selection.add(result.skipped.map(s => s.accountKey))
    await load()
  } finally {
    deletionSaving.value = false
  }
}

// The result line, "N deleted, M skipped." (or undeleted), noting when the skipped stay selected.
function resultKey(result) {
  if (result.action === 'delete') return result.skipped.length ? 'pages.accountProgressList.resultDeletedStillSelected' : 'pages.accountProgressList.resultDeleted'
  return result.skipped.length ? 'pages.accountProgressList.resultUndeletedStillSelected' : 'pages.accountProgressList.resultUndeleted'
}

function deletedNote(account) {
  if (!account.isDeleted) return ''
  const parts = []
  if (account.deletedByName) {
    parts.push(account.deletionReason
      ? t('pages.accountProgressList.deletedByWithReason', { name: account.deletedByName, reason: account.deletionReason })
      : t('pages.accountProgressList.deletedBy', { name: account.deletedByName }))
  }
  if (account.isDeletedInSource) {
    parts.push(account.isInIgnoredSafe ? t('pages.accountProgressList.notImportedIgnoredSafe') : t('pages.accountProgressList.deletedInSource', { source: isFeedAccount(account) ? account.sourceSystemName : 'CyberArk' }))
  }
  return parts.join('. ')
}

// D-212: accounts from a data feed (another system), as opposed to CyberArk or AD Account Discovery.
const nonFeedSources = ['PRIVCLOUD', 'SELFHOSTED', 'DISCOVERY']
function isFeedAccount(account) {
  return !!account.sourceSystemCode && !nonFeedSources.includes(account.sourceSystemCode)
}
const pageKeys = computed(() => accounts.value.map(a => a.accountKey))

const keysUrl = computed(() => `/api/account-progress/keys?${currentFilterParams().toString()}`)

function currentFilterParams() {
  const params = new URLSearchParams()
  if (stageFilter.value) params.set('stage', stageFilter.value)
  if (statusFilter.value) params.set('status', statusFilter.value)
  if (riskLevelFilter.value) params.set('riskLevel', riskLevelFilter.value)
  if (ownerFilter.value) params.set('owner', ownerFilter.value)
  if (searchFilter.value.trim()) params.set('search', searchFilter.value.trim())
  if (deletedFilter.value !== 'Hide') params.set('deleted', deletedFilter.value)
  if (linkedFilter.value !== 'Hide') params.set('linked', linkedFilter.value)
  return params
}


const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const latestRequest = useLatestRequest()
const pageSizeStore = usePageSizeStore()

// D-124 Phase 3: pagination -- page is local to this page (not persisted);
// pageSize comes from the shared, server-persisted pageSize store.
const page = ref(1)
const pageCount = computed(() => Math.max(1, Math.ceil((filteredCount.value ?? 0) / pageSizeStore.current)))

function onPageChange(newPage) {
  page.value = newPage
  load()
}

function onPageSizeChange() {
  page.value = 1
  load()
}

const accounts = ref([])
const referenceData = ref({})
const error = ref(null)
const loading = ref(true)

const stageFilter = ref('')
const statusFilter = ref('')
const riskLevelFilter = ref('')
const ownerFilter = ref('')
// D-178: one box matching Username or Address ("contains"), server-side.
const searchFilter = ref('')
// D-185: deleted accounts are hidden unless shown here.
const deletedFilter = ref('Hide')
// D-212: a feed account linked to a CyberArk account is hidden unless shown here.
const linkedFilter = ref('Hide')

// Each entry: { field, descending }. Order in this array IS sort priority.
const sortColumns = ref([])

const columns = [
  { field: 'userName', labelKey: 'pages.accountProgressList.colUsername' },
  { field: 'address', labelKey: 'pages.accountProgressList.colAddress' },
  { field: 'stageName', labelKey: 'pages.accountProgressList.colStage' },
  { field: 'statusName', labelKey: 'pages.accountProgressList.colStatus' },
  { field: 'riskLevelName', labelKey: 'pages.accountProgressList.colRiskLevel' },
  { field: 'ownerName', labelKey: 'pages.accountProgressList.colOwner' },
  { field: 'targetRemediationDate', labelKey: 'pages.accountProgressList.colTargetRemediationDate' },
  { field: 'effectiveRiskScore', labelKey: 'pages.accountProgressList.colRiskScore' },
  { field: 'riskScoreBandName', labelKey: 'pages.accountProgressList.colRiskBand' }
]

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

// D-92 (ARIA APG Sortable Table pattern): aria-sort only ever reflects the
// primary sort key -- aria-sort has no "this is the secondary key" value,
// so a secondary sort column (idx > 0) still reports "none" here even
// though sortIndicator() above shows it a numbered arrow visually.
function ariaSortFor(field) {
  if (sortColumns.value.length === 0 || sortColumns.value[0].field !== field) return 'none'
  return sortColumns.value[0].descending ? 'descending' : 'ascending'
}

function toggleSort(field, event) {
  const existingIndex = sortColumns.value.findIndex(s => s.field === field)

  if (!event.shiftKey) {
    // Plain click: this column becomes the only sort key. Clicking the
    // same column again (when it's already the sole key) flips direction.
    if (existingIndex === 0 && sortColumns.value.length === 1) {
      sortColumns.value = [{ field, descending: !sortColumns.value[0].descending }]
    } else {
      sortColumns.value = [{ field, descending: false }]
    }
    return
  }

  // Shift-click: add/toggle this column as an additional sort key.
  if (existingIndex === -1) {
    sortColumns.value = [...sortColumns.value, { field, descending: false }]
  } else {
    const updated = [...sortColumns.value]
    updated[existingIndex] = { ...updated[existingIndex], descending: !updated[existingIndex].descending }
    sortColumns.value = updated
  }
}

const sortQueryParam = computed(() => formatSort(sortColumns.value))

// UI review W2-2: the filters, sort and page are in the page address (?stage=...&sort=...&page=2), so a filtered list
// survives opening an account and coming back, a reload and a pasted link, and the Dashboard and the stage/status
// report can link straight to a stage. Defaults are left out of the address.
const LIST_ROUTE = 'account-progress-list'
const QUERY_DEFAULTS = { deleted: 'Hide', linked: 'Hide', page: 1 }
let applyingAddress = false

function applyAddress(query) {
  stageFilter.value = readText(query, 'stage')
  statusFilter.value = readText(query, 'status')
  riskLevelFilter.value = readText(query, 'riskLevel')
  ownerFilter.value = readText(query, 'owner')
  searchFilter.value = readText(query, 'search')
  deletedFilter.value = readOneOf(query, 'deleted', ['Hide', 'Show', 'Only'], 'Hide')
  linkedFilter.value = readOneOf(query, 'linked', ['Hide', 'Show'], 'Hide')
  sortColumns.value = parseSort(readText(query, 'sort'), columns.map(c => c.field))
  page.value = readPage(query)
}

function currentAddress() {
  return compactQuery({
    stage: stageFilter.value, status: statusFilter.value, riskLevel: riskLevelFilter.value, owner: ownerFilter.value,
    search: searchFilter.value.trim(), deleted: deletedFilter.value, linked: linkedFilter.value, sort: sortQueryParam.value, page: page.value
  }, QUERY_DEFAULTS)
}

applyAddress(route.query)
listState.remember(LIST_ROUTE, currentAddress())

function writeAddress() {
  if (applyingAddress) return
  const query = currentAddress()
  listState.remember(LIST_ROUTE, query)
  if (!sameQuery(query, route.query)) router.replace({ query })
}

async function load() {
  const request = latestRequest.begin()
  loading.value = true
  error.value = null
  try {
    const params = currentFilterParams()
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const response = await fetch(`/api/account-progress?${params.toString()}`)
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    const body = await response.json()
    if (!latestRequest.isCurrent(request)) return
    readTotalCount(response)
    accounts.value = body
  } catch (err) {
    if (latestRequest.isCurrent(request)) error.value = err.message
  } finally {
    if (latestRequest.isCurrent(request)) loading.value = false
  }
}

onMounted(async () => {
  try {
    const refResponse = await fetch('/api/account-progress/reference-data')
    if (refResponse.ok) referenceData.value = await refResponse.json()
  } catch {
    // Non-fatal -- filters just won't have dropdown options if this fails.
  }
  await load()
})

// D-124 Phase 3: a filter/sort change resets to page 1 -- see Targets.vue's
// identical comment for why page-size/Prev/Next changes are handled separately.
watch([stageFilter, statusFilter, riskLevelFilter, ownerFilter, searchFilter, deletedFilter, linkedFilter, sortQueryParam], () => {
  if (applyingAddress) return
  page.value = 1
  load()
})

watch([stageFilter, statusFilter, riskLevelFilter, ownerFilter, searchFilter, deletedFilter, linkedFilter, sortQueryParam, page], writeAddress, { flush: 'post' })

// Back and Forward (or a link to this list while it is open) change the address under the page: show what it says.
watch(() => route.query, async (query) => {
  if (route.name !== LIST_ROUTE || sameQuery(query, currentAddress())) return
  applyingAddress = true
  applyAddress(query)
  await nextTick()
  applyingAddress = false
  listState.remember(LIST_ROUTE, currentAddress())
  load()
})
</script>

<template>
  <div>
    <h1>{{ t('pages.accountProgressList.title') }}</h1>
    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.search') }}</span> <input v-model="searchFilter" type="search" :placeholder="t('pages.accountProgressList.searchPlaceholder')" /></label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.stage') }}</span>
        <select v-model="stageFilter">
          <option value="">{{ t('pages.accountProgressList.all') }}</option>
          <option v-for="opt in referenceData.dim_blueprint_stage ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.status') }}</span>
        <select v-model="statusFilter">
          <option value="">{{ t('pages.accountProgressList.all') }}</option>
          <option v-for="opt in referenceData.dim_progress_status ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.riskLevel') }}</span>
        <select v-model="riskLevelFilter">
          <option value="">{{ t('pages.accountProgressList.all') }}</option>
          <option v-for="opt in referenceData.dim_risk_level ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.owner') }}</span> <input v-model="ownerFilter" type="text" :placeholder="t('pages.accountProgressList.ownerPlaceholder')" /></label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.deletedAccounts') }}</span>
        <select v-model="deletedFilter">
          <option value="Hide">{{ t('pages.accountProgressList.hide') }}</option>
          <option value="Show">{{ t('pages.accountProgressList.show') }}</option>
          <option value="Only">{{ t('pages.accountProgressList.onlyDeleted') }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressList.linkedFeedAccounts') }}</span>
        <select v-model="linkedFilter">
          <option value="Hide">{{ t('pages.accountProgressList.hide') }}</option>
          <option value="Show">{{ t('pages.accountProgressList.show') }}</option>
        </select>
      </label>
    </p>
    <SelectionBar v-if="canEdit || canDelete" :selection="selection" :page-keys="pageKeys" :filtered-count="filteredCount"
                  :keys-url="keysUrl" :toggle-label="t('pages.accountProgressList.selectToggle')">
      <button v-if="canEdit" type="button" class="btn-primary" :disabled="selection.count === 0" @click="router.push({ name: 'account-progress-bulk-edit' })">{{ t('pages.accountProgressList.bulkEdit') }}</button>
      <button v-if="canDelete" type="button" :disabled="selection.count === 0" @click="startDeletion('delete')">{{ t('pages.accountProgressList.deleteEllipsis') }}</button>
      <button v-if="canDelete" type="button" :disabled="selection.count === 0" @click="startDeletion('undelete')">{{ t('pages.accountProgressList.undeleteEllipsis') }}</button>
    </SelectionBar>
    <form v-if="deletion" class="deletion-form" @submit.prevent="applyDeletion">
      <label class="field-label">
        <span class="field-label-text">{{ deletion.action === 'delete'
          ? t('pages.accountProgressList.reasonDelete', { count: formatCount(selection.count) }, selection.count)
          : t('pages.accountProgressList.reasonUndelete', { count: formatCount(selection.count) }, selection.count) }}</span>
        <input v-model="deletion.reason" size="60" maxlength="1000" required />
      </label>
      <button type="submit" class="btn-primary" :disabled="deletionSaving || !deletion.reason.trim()">{{ deletion.action === 'delete' ? t('common.delete') : t('pages.accountProgressList.undelete') }}</button>
      <button type="button" @click="deletion = null">{{ t('common.cancel') }}</button>
      <p v-if="deletionError" role="alert">{{ deletionError }}</p>
    </form>
    <div v-if="deletionResult" role="status">
      <p>{{ t(resultKey(deletionResult), { changed: formatCount(deletionResult.changed), skipped: formatCount(deletionResult.skipped.length) }) }}</p>
      <ul v-if="deletionResult.skipped.length">
        <li v-for="s in deletionResult.skipped" :key="s.accountKey">{{ s.accountName ?? s.accountKey }}: {{ s.reason }}</li>
      </ul>
    </div>
    <FilterCountSummary :shown="accounts.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('pages.accountProgressList.couldNotLoad', { error }) }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-if="selection.enabled"><span class="visually-hidden">{{ t('pages.accountProgressList.selected') }}</span></th>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
        </tr>
      </thead>
      <tbody>
        <template v-for="account in accounts" :key="account.accountKey">
          <tr>
            <td v-if="selection.enabled">
              <input type="checkbox" :checked="selection.isSelected(account.accountKey)" :aria-label="account.address ? t('pages.accountProgressList.selectRowOn', { name: account.userName || account.accountName, address: account.address }) : t('pages.accountProgressList.selectRow', { name: account.userName || account.accountName })" @change="selection.toggle(account.accountKey)" />
            </td>
            <td>
              <!-- D-191: an account with no username links by its account name, so it can always be opened. -->
              <router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.userName || account.accountName }}</router-link>
              <span v-if="!account.userName" class="no-username">{{ ' ' + t('pages.accountProgressList.noUsername') }}</span>
              <span v-if="decomNote(account)" class="decom-marker" :title="decomNote(account)"><span aria-hidden="true">⚠</span><span class="visually-hidden">{{ t('pages.accountProgressList.flaggedForDeletion', { note: decomNote(account) }) }}</span></span>
              <span v-if="account.isDeleted" class="deleted-badge" :title="deletedNote(account)">{{ t('pages.accountProgressList.deleted') }}<span class="visually-hidden">{{ t('pages.accountProgressList.deletedHidden', { note: deletedNote(account) }) }}</span></span>
              <span v-if="isFeedAccount(account)" class="source-badge">{{ t('pages.accountProgressList.fromSource', { source: account.sourceSystemName }) }}</span>
              <router-link v-if="account.linkedToAccountKey" class="linked-badge" :to="{ name: 'account-progress-detail', params: { accountKey: account.linkedToAccountKey } }"
                :title="t('pages.accountProgressList.linkedTitle', { name: account.linkedToAccountName })">{{ t('pages.accountProgressList.linked') }}<span class="visually-hidden">{{ t('pages.accountProgressList.linkedHidden', { name: account.linkedToAccountName }) }}</span></router-link>
            </td>
            <td>{{ account.address }}</td>
            <td>{{ account.stageName }}</td>
            <td>{{ account.statusName }}</td>
            <td>{{ account.riskLevelName }}</td>
            <td>{{ account.ownerName }}</td>
            <td>{{ formatDate(account.targetRemediationDate) }}</td>
            <td>{{ account.effectiveRiskScore ?? '—' }}</td>
            <td>{{ account.riskScoreBandName ?? '—' }}</td>
          </tr>
        </template>
      </tbody>
    </table>
    <EmptyListMessage :show="!loading && !error && accounts.length === 0" :message="totalCount === 0 ? t('pages.accountProgressList.emptyNone') : t('pages.accountProgressList.emptyFiltered')" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p><small>{{ t('pages.accountProgressList.sortHint') }}</small></p>
  </div>
</template>

<style scoped>
.no-username {
  font-size: 0.85em;
  opacity: 0.8;
}
.decom-marker {
  margin-left: 0.4rem;
  cursor: help;
}
.deleted-badge {
  margin-left: 0.5rem;
  padding: 0 0.3rem;
  border: 1px solid currentColor;
  border-radius: 0.25rem;
  font-size: 0.85em;
}
.source-badge,
.linked-badge {
  margin-left: 0.5rem;
  font-size: 0.85em;
  opacity: 0.8;
}
.deletion-form {
  margin: 0.5rem 0;
}
</style>
