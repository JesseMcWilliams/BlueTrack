<script setup>
// D-42: multiple simultaneous filter layers (stage/status/riskLevel/owner,
// stacked with AND) plus multi-column sort -- click a column header to sort
// by just that column; shift-click another header to add it as a secondary
// sort key without losing the first (badges show the resulting priority).
import { ref, computed, watch, onMounted } from 'vue'
import { formatDate } from '../utils/formatDate'
import { useTotalCount } from '../composables/useTotalCount'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'
import { useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import { useAccountSelectionStore } from '../stores/accountSelection'
import { decomNote } from '../utils/decomNote'

// D-182: bulk edit. "Select accounts" turns on a checkbox column; the
// selection (a store) survives paging, sorting and filtering. Select all
// on page / Invert apply to the current page; Select all matching asks the
// API for every account matching the filters, up to the bulk-edit limit.
const router = useRouter()
const rights = useRightsStore()
const selection = useAccountSelectionStore()
const selectAllMessage = ref(null)
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
      deletionError.value = problem?.detail ?? `Failed: ${response.status}`
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

function deletedNote(account) {
  if (!account.isDeleted) return ''
  const parts = []
  if (account.deletedByName) parts.push(`Deleted in BlueTrack by ${account.deletedByName}${account.deletionReason ? `: ${account.deletionReason}` : ''}`)
  if (account.isDeletedInSource) parts.push(account.isInIgnoredSafe ? 'Not imported (ignored safe)' : 'Deleted in CyberArk')
  return parts.join('. ')
}
const pageKeys = computed(() => accounts.value.map(a => a.accountKey))
const selectedOnPage = computed(() => pageKeys.value.filter(k => selection.isSelected(k)).length)

function currentFilterParams() {
  const params = new URLSearchParams()
  if (stageFilter.value) params.set('stage', stageFilter.value)
  if (statusFilter.value) params.set('status', statusFilter.value)
  if (riskLevelFilter.value) params.set('riskLevel', riskLevelFilter.value)
  if (ownerFilter.value) params.set('owner', ownerFilter.value)
  if (searchFilter.value.trim()) params.set('search', searchFilter.value.trim())
  if (deletedFilter.value !== 'Hide') params.set('deleted', deletedFilter.value)
  return params
}

async function selectAllMatching() {
  selectAllMessage.value = null
  const response = await fetch(`/api/account-progress/keys?${currentFilterParams().toString()}`)
  if (!response.ok) {
    selectAllMessage.value = `Could not select: ${response.status}`
    return
  }
  const result = await response.json()
  if (result.matchingCount > result.maxAccounts) {
    selectAllMessage.value = `${result.matchingCount} accounts match, more than the bulk edit limit of ${result.maxAccounts}. Narrow the filters first.`
    return
  }
  selection.add(result.accountKeys)
}

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
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

// Each entry: { field, descending }. Order in this array IS sort priority.
const sortColumns = ref([])

const columns = [
  { field: 'userName', label: 'Username' },
  { field: 'address', label: 'Address' },
  { field: 'stageName', label: 'Stage' },
  { field: 'statusName', label: 'Status' },
  { field: 'riskLevelName', label: 'Risk Level' },
  { field: 'ownerName', label: 'Owner' },
  { field: 'targetRemediationDate', label: 'Target Remediation Date' },
  { field: 'effectiveRiskScore', label: 'Risk Score' },
  { field: 'riskScoreBandName', label: 'Risk Band' }
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

const sortQueryParam = computed(() =>
  sortColumns.value.map(s => `${s.field}:${s.descending ? 'desc' : 'asc'}`).join(','))

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = currentFilterParams()
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const response = await fetch(`/api/account-progress?${params.toString()}`)
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    readTotalCount(response)
    accounts.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
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
watch([stageFilter, statusFilter, riskLevelFilter, ownerFilter, searchFilter, deletedFilter, sortQueryParam], () => {
  page.value = 1
  load()
})
</script>

<template>
  <div>
    <h1>Account Progress</h1>
    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">Search:</span> <input v-model="searchFilter" type="search" placeholder="username or address..." /></label>
      <label class="field-label"><span class="field-label-text">Stage:</span>
        <select v-model="stageFilter">
          <option value="">All</option>
          <option v-for="opt in referenceData.dim_blueprint_stage ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Status:</span>
        <select v-model="statusFilter">
          <option value="">All</option>
          <option v-for="opt in referenceData.dim_progress_status ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Risk Level:</span>
        <select v-model="riskLevelFilter">
          <option value="">All</option>
          <option v-for="opt in referenceData.dim_risk_level ?? []" :key="opt.key" :value="opt.name">{{ opt.name }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Owner:</span> <input v-model="ownerFilter" type="text" placeholder="contains..." /></label>
      <label class="field-label"><span class="field-label-text">Deleted accounts:</span>
        <select v-model="deletedFilter">
          <option value="Hide">Hide</option>
          <option value="Show">Show</option>
          <option value="Only">Only deleted</option>
        </select>
      </label>
    </p>
    <div v-if="canEdit || canDelete" class="selection-bar">
      <label><input type="checkbox" :checked="selection.enabled" @change="selection.setEnabled($event.target.checked)" /> Select accounts for bulk actions</label>
      <template v-if="selection.enabled">
        <strong role="status">{{ selection.count }} selected</strong><span v-if="selection.count"> ({{ selectedOnPage }} on this page)</span>
        <button type="button" @click="selection.add(pageKeys)">Select all on page</button>
        <button type="button" @click="selectAllMatching">Select all matching ({{ filteredCount ?? 0 }})</button>
        <button type="button" @click="selection.invert(pageKeys)">Invert selection on page</button>
        <button type="button" :disabled="selection.count === 0" @click="selection.clear()">Clear</button>
        <button v-if="canEdit" type="button" class="btn-primary" :disabled="selection.count === 0" @click="router.push({ name: 'account-progress-bulk-edit' })">Bulk edit…</button>
        <button v-if="canDelete" type="button" :disabled="selection.count === 0" @click="startDeletion('delete')">Delete…</button>
        <button v-if="canDelete" type="button" :disabled="selection.count === 0" @click="startDeletion('undelete')">Undelete…</button>
      </template>
    </div>
    <form v-if="deletion" class="deletion-form" @submit.prevent="applyDeletion">
      <label class="field-label">
        <span class="field-label-text">Reason to {{ deletion.action }} {{ selection.count }} account{{ selection.count === 1 ? '' : 's' }}:</span>
        <input v-model="deletion.reason" size="60" maxlength="1000" required />
      </label>
      <button type="submit" class="btn-primary" :disabled="deletionSaving || !deletion.reason.trim()">{{ deletion.action === 'delete' ? 'Delete' : 'Undelete' }}</button>
      <button type="button" @click="deletion = null">Cancel</button>
      <p v-if="deletionError" role="alert">{{ deletionError }}</p>
    </form>
    <div v-if="deletionResult" role="status">
      <p>{{ deletionResult.changed }} {{ deletionResult.action === 'delete' ? 'deleted' : 'undeleted' }}, {{ deletionResult.skipped.length }} skipped<template v-if="deletionResult.skipped.length"> (still selected)</template>.</p>
      <ul v-if="deletionResult.skipped.length">
        <li v-for="s in deletionResult.skipped" :key="s.accountKey">{{ s.accountName ?? s.accountKey }}: {{ s.reason }}</li>
      </ul>
    </div>
    <p v-if="selectAllMessage" role="alert">{{ selectAllMessage }}</p>
    <FilterCountSummary :shown="accounts.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load accounts: {{ error }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-if="selection.enabled"><span class="visually-hidden">Selected</span></th>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
        </tr>
      </thead>
      <tbody>
        <template v-for="account in accounts" :key="account.accountKey">
          <tr>
            <td v-if="selection.enabled">
              <input type="checkbox" :checked="selection.isSelected(account.accountKey)" :aria-label="`Select ${account.userName} on ${account.address}`" @change="selection.toggle(account.accountKey)" />
            </td>
            <td>
              <router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.userName }}</router-link>
              <span v-if="decomNote(account)" class="decom-marker" :title="decomNote(account)"><span aria-hidden="true">⚠</span><span class="visually-hidden">Flagged for deletion: {{ decomNote(account) }}</span></span>
              <span v-if="account.isDeleted" class="deleted-badge" :title="deletedNote(account)">Deleted<span class="visually-hidden">: {{ deletedNote(account) }}</span></span>
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
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>
  </div>
</template>

<style scoped>
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
.deletion-form {
  margin: 0.5rem 0;
}
.selection-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem 1rem;
  margin: 0.5rem 0;
}
</style>
