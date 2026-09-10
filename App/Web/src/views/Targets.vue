<script setup>
// Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for
// the Target inventory. Each Target's identifiers (web.target_identifier)
// are a small nested collection edited alongside the Target itself --
// replace-all-on-save (TargetRepository's own comment on why).
// Phase B adds bulk CSV upload (Target inventory + direct Account->Target
// links) against RiskScoringImportController.
//
// D-121: promoted out of the Admin hub to a top-level nav entry (Analyst
// now has ManageTargets too, same as Admin) -- and, since this page
// previously had zero filter/sort support at all, this adds it fresh:
// stacked filters (type/application) plus sortable column headers, same
// click/shift-click multi-column sort pattern as the D-42 pages, plus the
// app-wide "Showing N of M total" count summary (X-Total-Count).
//
// D-124 Phase 2: TargetType is now a real dimension table
// (web.dim_target_type, code + DisplayName) instead of the old hardcoded
// TARGET_TYPES array with zero database enforcement -- fetched from
// GET /api/admin/targets/target-types, same pattern as AccessGroups.vue's
// SOR Type dropdown. The filter/form <select>s bind TargetTypeKey (the FK),
// not the raw code string, and show DisplayName ('LDAP Directory', not
// 'LdapDirectory'; 'Active Directory' is a new, distinct entry).
//
// D-124 Phase 4: Add/Edit moved to its own routed page (TargetEdit.vue,
// target-create/target-edit) -- this page no longer owns an inline
// editing/startCreate/startEdit/cancelEdit form at all; "+ New Target" and
// each row's "Edit" are now router-link navigations.
//
// D-124 Phase 5: the 2 always-visible inline "Bulk Import" sections (and
// their importFile/onInventoryFileChange/onAccountTargetFileChange script
// logic) moved off this page onto TargetsBulkImport.vue, reached via the
// "Bulk Actions" header link below -- the "Link a Single Account to a
// Target" section stays here, since it's a single-record form (no file
// upload), not a bulk import.
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useTotalCount } from '../composables/useTotalCount'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'

const router = useRouter()

const linkAccountName = ref('')
const linkTargetKey = ref(null)
const linkError = ref(null)
const linkSaved = ref(false)

async function createAccountTargetLink() {
  linkError.value = null
  linkSaved.value = false
  const response = await fetch('/api/admin/risk-scoring/account-target-links', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ accountName: linkAccountName.value, targetKey: linkTargetKey.value })
  })
  if (!response.ok) {
    linkError.value = `Link failed: ${response.status}`
    return
  }
  linkSaved.value = true
  linkAccountName.value = ''
  linkTargetKey.value = null
}

const items = ref([])
const applications = ref([])
const identifierTypes = ref([])
// D-124 Phase 2: web.dim_target_type is now a real dimension table (code +
// DisplayName) fetched from the API, replacing the old hardcoded
// TARGET_TYPES array -- the filter/form <select>s bind TargetTypeKey, not
// the raw code string, and show DisplayName ('LDAP Directory', not
// 'LdapDirectory').
const targetTypes = ref([])
const error = ref(null)
const loading = ref(true)

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

const typeFilter = ref('')
const applicationFilter = ref('')

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

const sortColumns = ref([])
const columns = [
  { field: 'targetName', label: 'Name' },
  { field: 'targetType', label: 'Type' },
  { field: 'applicationName', label: 'Application' },
  { field: 'riskScore', label: 'Risk Score' }
]

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
  const existingIndex = sortColumns.value.findIndex(s => s.field === field)

  if (!event.shiftKey) {
    if (existingIndex === 0 && sortColumns.value.length === 1) {
      sortColumns.value = [{ field, descending: !sortColumns.value[0].descending }]
    } else {
      sortColumns.value = [{ field, descending: false }]
    }
    return
  }

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
  try {
    const params = new URLSearchParams()
    if (typeFilter.value) params.set('targetTypeKey', typeFilter.value)
    if (applicationFilter.value) params.set('applicationKey', applicationFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const [targetsResponse, typesResponse] = await Promise.all([
      fetch(`/api/admin/targets?${params.toString()}`),
      fetch('/api/admin/targets/identifier-types')
    ])
    if (!targetsResponse.ok) throw new Error(`Request failed: ${targetsResponse.status}`)
    if (!typesResponse.ok) throw new Error(`Request failed: ${typesResponse.status}`)
    readTotalCount(targetsResponse)
    items.value = await targetsResponse.json()
    identifierTypes.value = await typesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const appsResponse = await fetch('/api/applications')
    if (appsResponse.ok) applications.value = await appsResponse.json()
  } catch {
    // Non-fatal -- the application filter just won't have dropdown options if this fails.
  }
  try {
    const targetTypesResponse = await fetch('/api/admin/targets/target-types')
    if (targetTypesResponse.ok) targetTypes.value = await targetTypesResponse.json()
  } catch {
    // Non-fatal -- the Type dropdown just won't have options if this fails.
  }
  await load()
})

// D-124 Phase 3: a filter/sort change resets to page 1 (the previous page
// number might not even exist under the new filter) -- a page-size change
// is handled separately by onPageSizeChange, and a plain Prev/Next click by
// onPageChange, neither of which should reset anything else.
watch([typeFilter, applicationFilter, sortQueryParam], () => {
  page.value = 1
  load()
})

async function remove(item) {
  const response = await fetch(`/api/admin/targets/${item.targetKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status} (a target still referenced by an Access Group or Account mapping can't be deleted)`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h1>Targets</h1>
    <p>Any final destination an account's access leads to -- a server, database, application, or other endpoint. Risk score (0-1000) is always analyst-set here, regardless of how the row itself was created.</p>
    <p>
      <button type="button" class="btn-primary" @click="router.push({ name: 'target-create' })">+ New Target</button>
      <button type="button" @click="router.push({ name: 'targets-bulk-import' })">Bulk Actions</button>
    </p>
    <p v-if="error" role="alert">{{ error }}</p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">Type:</span>
        <select v-model="typeFilter">
          <option value="">All</option>
          <option v-for="type in targetTypes" :key="type.targetTypeKey" :value="type.targetTypeKey">{{ type.displayName }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Application:</span>
        <select v-model="applicationFilter">
          <option value="">All</option>
          <option v-for="app in applications" :key="app.applicationKey" :value="app.applicationKey">{{ app.applicationName }}</option>
        </select>
      </label>
    </p>
    <FilterCountSummary :shown="items.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />

    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <table>
        <thead>
          <tr>
            <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
              <button type="button" @click="toggleSort(col.field, $event)">
                {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
              </button>
            </th>
            <th>Identifiers</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.targetKey">
            <td><router-link :to="{ name: 'target-edit', params: { targetKey: item.targetKey } }">{{ item.targetName }}</router-link></td>
            <td>{{ item.targetTypeDisplayName }}</td>
            <td>{{ item.applicationName }}</td>
            <td>{{ item.riskScore }}</td>
            <td>{{ item.identifiers.map(i => `${i.identifierType}=${i.identifierValue}`).join(', ') }}</td>
            <td>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
      <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
      <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>

      <h4>Link a Single Account to a Target</h4>
      <p v-if="linkError" role="alert">{{ linkError }}</p>
      <p v-if="linkSaved" role="status">Linked.</p>
      <form class="filter-row" @submit.prevent="createAccountTargetLink">
        <label class="field-label"><span class="field-label-text">Account Name:</span> <input v-model="linkAccountName" required /></label>
        <label class="field-label">
          <span class="field-label-text">Target:</span>
          <select v-model="linkTargetKey" required>
            <option :value="null" disabled>(choose)</option>
            <option v-for="item in items" :key="item.targetKey" :value="item.targetKey">{{ item.targetName }}</option>
          </select>
        </label>
        <button type="submit" class="btn-primary">Link</button>
      </form>
    </template>
  </div>
</template>
