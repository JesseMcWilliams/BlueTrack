<script setup>
// Design_Risk-Scoring.md, D-101-105, Phase A: single add/edit/delete for
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
import { useLatestRequest } from '../composables/useLatestRequest'
import { confirmDelete } from '../composables/useConfirmDialog'
import { usePageSizeStore } from '../stores/pageSize'
import FilterCountSummary from '../components/FilterCountSummary.vue'
import Pager from '../components/Pager.vue'
import SelectionBar from '../components/SelectionBar.vue'
import BulkFieldEditor from '../components/BulkFieldEditor.vue'
import BulkReasonForm from '../components/BulkReasonForm.vue'
import BulkResultSummary from '../components/BulkResultSummary.vue'
import { useListSelectionStore } from '../stores/listSelection'
import { useBulkAction } from '../composables/useBulkAction'
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../components/EmptyListMessage.vue'

const { t } = useI18n()

const router = useRouter()
const items = ref([])
const sorTypes = ref([])
const error = ref(null)
const loading = ref(true)

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const latestRequest = useLatestRequest()
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
  { field: 'groupName', labelKey: 'pages.accessGroups.colName' },
  { field: 'groupIdentifier', labelKey: 'pages.accessGroups.colIdentifier' },
  { field: 'groupScope', labelKey: 'pages.accessGroups.colScope' },
  { field: 'sorTypeName', labelKey: 'pages.accessGroups.colSorType' },
  { field: 'baseRiskScore', labelKey: 'pages.accessGroups.colBaseRisk' },
  { field: 'computedRiskScore', labelKey: 'pages.accessGroups.colComputedRisk' }
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
  const request = latestRequest.begin()
  loading.value = true
  try {
    const params = new URLSearchParams()
    if (scopeFilter.value) params.set('groupScope', scopeFilter.value)
    if (sorTypeFilter.value) params.set('sorTypeName', sorTypeFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const groupsResponse = await fetch(`/api/admin/access-groups?${params.toString()}`)
    if (!groupsResponse.ok) throw new Error(t('common.requestFailed', { status: groupsResponse.status }))
    const body = await groupsResponse.json()
    if (!latestRequest.isCurrent(request)) return
    readTotalCount(groupsResponse)
    items.value = body
  } catch (err) {
    if (latestRequest.isCurrent(request)) error.value = err.message
  } finally {
    if (latestRequest.isCurrent(request)) loading.value = false
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

// D-190: bulk edit and bulk delete (reason required) through the shared
// selection bar, as on Account Progress.
const selection = useListSelectionStore('accessGroups')
const { saving: bulkSaving, result: bulkResult, error: bulkError, run: runBulk } = useBulkAction(selection)
const bulkPanel = ref(null) // 'edit' | 'delete'
const pageKeys = computed(() => items.value.map(i => i.accessGroupKey))
const keysUrl = computed(() => {
  const params = new URLSearchParams()
  if (scopeFilter.value) params.set('groupScope', scopeFilter.value)
  if (sorTypeFilter.value) params.set('sorTypeName', sorTypeFilter.value)
  return `/api/admin/access-groups/keys?${params.toString()}`
})
const bulkFields = computed(() => [
  { field: 'GroupScope', label: t('pages.accessGroups.fieldScope'), type: 'select', required: true, options: [{ value: 'Domain', label: t('pages.accessGroups.domain') }, { value: 'Local', label: t('pages.accessGroups.local') }] },
  { field: 'BaseRiskScore', label: t('pages.accessGroups.fieldBaseRiskScore'), type: 'number' },
  { field: 'Description', label: t('common.description'), type: 'text' }
])

async function bulkEdit(payload) {
  if (await runBulk('/api/admin/access-groups/bulk-edit', payload, t('pages.accessGroups.verbUpdated'))) {
    bulkPanel.value = null
    await load()
  }
}

async function bulkDelete(reason) {
  if (await runBulk('/api/admin/access-groups/bulk-delete', { reason }, t('pages.accessGroups.verbDeleted'))) {
    bulkPanel.value = null
    await load()
  }
}

async function remove(item) {
  // D-129/D-131: a structured Name/Scope/Address/Source summary (rendered
  // indented, set off from the title/warning) -- Access Group's own
  // fields map onto this exactly (GroupScope/SorAddress/DiscoverySource).
  const message = {
    title: t('pages.accessGroups.confirmTitle'),
    details: [
      t('pages.accessGroups.confirmName', { name: item.groupName }),
      t('pages.accessGroups.confirmScope', { scope: item.groupScope }),
      t('pages.accessGroups.confirmAddress', { address: item.sorAddress ?? '—' }),
      t('pages.accessGroups.confirmSource', { source: item.discoverySource ?? '—' })
    ],
    warning: t('pages.accessGroups.confirmWarning')
  }
  if (!(await confirmDelete(message))) return
  const response = await fetch(`/api/admin/access-groups/${item.accessGroupKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('pages.accessGroups.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h1>{{ t('pages.accessGroups.title') }}</h1>
    <p>{{ t('pages.accessGroups.intro') }}</p>
    <p>
      <button type="button" class="btn-primary" @click="router.push({ name: 'access-group-create' })">{{ t('pages.accessGroups.newAccessGroup') }}</button>
      <button type="button" @click="router.push({ name: 'access-groups-bulk-import' })">{{ t('pages.accessGroups.bulkActions') }}</button>
    </p>
    <p v-if="error" role="alert">{{ error }}</p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">{{ t('pages.accessGroups.scope') }}</span>
        <select v-model="scopeFilter">
          <option value="">{{ t('pages.accessGroups.all') }}</option>
          <option value="Domain">{{ t('pages.accessGroups.domain') }}</option>
          <option value="Local">{{ t('pages.accessGroups.local') }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accessGroups.sorType') }}</span>
        <select v-model="sorTypeFilter">
          <option value="">{{ t('pages.accessGroups.all') }}</option>
          <option v-for="type in sorTypes" :key="type.sorTypeKey" :value="type.sorTypeName">{{ type.sorTypeName }}</option>
        </select>
      </label>
    </p>
    <SelectionBar :selection="selection" :page-keys="pageKeys" :filtered-count="filteredCount" :keys-url="keysUrl" :toggle-label="t('pages.accessGroups.selectToggle')">
      <button type="button" class="btn-primary" :disabled="selection.count === 0" @click="bulkPanel = 'edit'">{{ t('pages.accessGroups.bulkEdit') }}</button>
      <button type="button" :disabled="selection.count === 0" @click="bulkPanel = 'delete'">{{ t('pages.accessGroups.deleteEllipsis') }}</button>
    </SelectionBar>
    <BulkFieldEditor v-if="bulkPanel === 'edit'" :fields="bulkFields" :count="selection.count" :saving="bulkSaving" @apply="bulkEdit" @cancel="bulkPanel = null" />
    <BulkReasonForm v-if="bulkPanel === 'delete'" :action="t('pages.accessGroups.deleteAction', { count: formatCount(selection.count) })" :button-label="t('common.delete')" :saving="bulkSaving" @submit="bulkDelete" @cancel="bulkPanel = null" />
    <p v-if="bulkError" role="alert">{{ bulkError }}</p>
    <BulkResultSummary v-if="bulkResult" :result="bulkResult" />
    <FilterCountSummary :shown="items.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />

    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <table>
        <thead>
          <tr>
            <th v-if="selection.enabled"><span class="visually-hidden">{{ t('pages.accessGroups.selected') }}</span></th>
            <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
              <button type="button" @click="toggleSort(col.field, $event)">
                {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
              </button>
            </th>
            <th>{{ t('pages.accessGroups.colSorAddress') }}</th>
            <th>{{ t('pages.accessGroups.colDiscoverySource') }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.accessGroupKey">
            <td v-if="selection.enabled"><input type="checkbox" :checked="selection.isSelected(item.accessGroupKey)" :aria-label="t('pages.accessGroups.selectRow', { name: item.groupName })" @change="selection.toggle(item.accessGroupKey)" /></td>
            <td><router-link :to="{ name: 'access-group-edit', params: { accessGroupKey: item.accessGroupKey } }">{{ item.groupName }}</router-link></td>
            <td>{{ item.groupIdentifier }}</td>
            <td>{{ item.groupScope }}<span v-if="item.foundOnTargetName"> ({{ item.foundOnTargetName }})</span></td>
            <td>{{ item.sorTypeName ?? '—' }}</td>
            <td>{{ item.baseRiskScore }}</td>
            <td>{{ item.computedRiskScore ?? t('pages.accessGroups.notYetCalculated') }}<span v-if="item.isRiskScoreStale">{{ ' ' + t('pages.accessGroups.stale') }}</span></td>
            <td>{{ item.sorAddress ?? '—' }}</td>
            <td>{{ item.discoverySource ?? '—' }}</td>
            <td>
              <button @click="remove(item)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="!loading && items.length === 0" :message="totalCount === 0 ? t('pages.accessGroups.emptyNone') : t('pages.accessGroups.emptyFiltered')" />
      <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
      <p><small>{{ t('pages.accessGroups.sortHint') }}</small></p>
    </template>
  </div>
</template>
