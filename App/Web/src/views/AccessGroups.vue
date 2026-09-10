<script setup>
// Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for
// Access Groups -- a privileged-access group in the MANAGED environment
// (e.g. an AD "Server Admins" group), distinct from CyberArk's own
// dim_group (Safe permissions) and web.identity_group_role_map (this
// app's own login/authorization mapping). Phase B adds bulk CSV upload for
// the group inventory itself plus its two mapping feeds (which Targets it
// reaches, which Accounts belong to it).
//
// D-121: promoted out of the Admin hub to a top-level nav entry (Analyst
// now has ManageAccessGroups too, same as Admin); adds SOR Type (a new
// controlled dimension, web.dim_sor_type) and SOR Address (free text) --
// additive alongside the existing GroupScope/FoundOnTargetKey, which are
// unchanged -- plus surfaces the already-existing DiscoverySource column,
// previously never shown here. Also adds this page's first-ever filter/sort
// support (scope/SOR type, sortable column headers) and the app-wide
// "Showing N of M total" count summary (X-Total-Count).
//
// D-124 Phase 4: Add/Edit moved to its own routed page (AccessGroupEdit.vue,
// access-group-create/access-group-edit) -- this page no longer owns an
// inline editing/startCreate/startEdit/cancelEdit form at all; "+ New
// Access Group" and each row's "Edit" are now router-link navigations.
//
// D-124 Phase 5: the 3 always-visible inline "Bulk Import" sections (and
// their importState/onFileChange/importFile script logic) moved off this
// page onto AccessGroupsBulkImport.vue, reached via the "Bulk Actions"
// header link below.
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useTotalCount } from '../composables/useTotalCount'
import { confirmDelete } from '../composables/useConfirmDialog'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'

const router = useRouter()
const items = ref([])
const sorTypes = ref([])
const error = ref(null)
const loading = ref(true)

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()

const scopeFilter = ref('')
const sorTypeFilter = ref('')

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
  { field: 'groupName', label: 'Name' },
  { field: 'groupIdentifier', label: 'Identifier' },
  { field: 'groupScope', label: 'Scope' },
  { field: 'sorTypeName', label: 'SOR Type' },
  { field: 'baseRiskScore', label: 'Base Risk' },
  { field: 'computedRiskScore', label: 'Computed Risk' }
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
    if (scopeFilter.value) params.set('groupScope', scopeFilter.value)
    if (sorTypeFilter.value) params.set('sorTypeName', sorTypeFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const groupsResponse = await fetch(`/api/admin/access-groups?${params.toString()}`)
    if (!groupsResponse.ok) throw new Error(`Request failed: ${groupsResponse.status}`)
    readTotalCount(groupsResponse)
    items.value = await groupsResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const sorTypesResponse = await fetch('/api/admin/access-groups/sor-types')
    if (sorTypesResponse.ok) sorTypes.value = await sorTypesResponse.json()
  } catch {
    // Non-fatal -- the SOR Type dropdown just won't have options if this fails.
  }
  await load()
})

// D-124 Phase 3: a filter/sort change resets to page 1 -- see Targets.vue's
// identical comment for why page-size/Prev/Next changes are handled separately.
watch([scopeFilter, sorTypeFilter, sortQueryParam], () => {
  page.value = 1
  load()
})

async function remove(item) {
  if (!(await confirmDelete(`Delete Access Group "${item.groupName}"? This cannot be undone.`))) return
  const response = await fetch(`/api/admin/access-groups/${item.accessGroupKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status} (a group still referenced by a Target/Account mapping can't be deleted)`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h1>Access Groups</h1>
    <p>Privileged-access groups in the managed environment (e.g. an AD "Server Admins" group) -- not this app's own Safe-permission groups or its login/role mapping. Base risk score (0-1000) is analyst-set; the computed score (base plus reachable Targets) is calculated separately.</p>
    <p>
      <button type="button" class="btn-primary" @click="router.push({ name: 'access-group-create' })">+ New Access Group</button>
      <button type="button" @click="router.push({ name: 'access-groups-bulk-import' })">Bulk Actions</button>
    </p>
    <p v-if="error" role="alert">{{ error }}</p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">Scope:</span>
        <select v-model="scopeFilter">
          <option value="">All</option>
          <option value="Domain">Domain</option>
          <option value="Local">Local</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">SOR Type:</span>
        <select v-model="sorTypeFilter">
          <option value="">All</option>
          <option v-for="type in sorTypes" :key="type.sorTypeKey" :value="type.sorTypeName">{{ type.sorTypeName }}</option>
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
            <th>SOR Address</th>
            <th>Discovery Source</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.accessGroupKey">
            <td><router-link :to="{ name: 'access-group-edit', params: { accessGroupKey: item.accessGroupKey } }">{{ item.groupName }}</router-link></td>
            <td>{{ item.groupIdentifier }}</td>
            <td>{{ item.groupScope }}<span v-if="item.foundOnTargetName"> ({{ item.foundOnTargetName }})</span></td>
            <td>{{ item.sorTypeName ?? '—' }}</td>
            <td>{{ item.baseRiskScore }}</td>
            <td>{{ item.computedRiskScore ?? '(not yet calculated)' }}<span v-if="item.isRiskScoreStale"> (stale)</span></td>
            <td>{{ item.sorAddress ?? '—' }}</td>
            <td>{{ item.discoverySource ?? '—' }}</td>
            <td>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
      <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
      <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>
    </template>
  </div>
</template>
