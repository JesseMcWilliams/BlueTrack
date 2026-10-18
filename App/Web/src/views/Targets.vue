<script setup>
// Design_Risk-Scoring.md, D-101-105, Phase A: single add/edit/delete for
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
//
// D-229: a target linked to another (Target Match Review's Link) is hidden
// unless "Linked targets: Show" is chosen, as Account Progress's linked feed
// accounts (D-212); a shown one carries a Linked badge to its primary.
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

const { t } = useI18n()

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
    linkError.value = t('pages.targets.linkFailed', { status: response.status })
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
const latestRequest = useLatestRequest()
const pageSizeStore = usePageSizeStore()

const typeFilter = ref('')
const applicationFilter = ref('')
const linkedFilter = ref('Hide')

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
  { field: 'targetName', labelKey: 'pages.targets.colName' },
  { field: 'targetType', labelKey: 'pages.targets.colType' },
  { field: 'applicationName', labelKey: 'pages.targets.colApplication' },
  { field: 'riskScore', labelKey: 'pages.targets.colRiskScore' }
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
    if (typeFilter.value) params.set('targetTypeKey', typeFilter.value)
    if (applicationFilter.value) params.set('applicationKey', applicationFilter.value)
    if (linkedFilter.value !== 'Hide') params.set('linked', linkedFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const [targetsResponse, typesResponse] = await Promise.all([
      fetch(`/api/admin/targets?${params.toString()}`),
      fetch('/api/admin/targets/identifier-types')
    ])
    if (!targetsResponse.ok) throw new Error(t('common.requestFailed', { status: targetsResponse.status }))
    if (!typesResponse.ok) throw new Error(t('common.requestFailed', { status: typesResponse.status }))
    const targetsBody = await targetsResponse.json()
    const typesBody = await typesResponse.json()
    if (!latestRequest.isCurrent(request)) return
    readTotalCount(targetsResponse)
    items.value = targetsBody
    identifierTypes.value = typesBody
  } catch (err) {
    if (latestRequest.isCurrent(request)) error.value = err.message
  } finally {
    if (latestRequest.isCurrent(request)) loading.value = false
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
watch([typeFilter, applicationFilter, linkedFilter, sortQueryParam], () => {
  page.value = 1
  load()
})

// D-190: bulk edit and bulk delete (reason required) through the shared
// selection bar, as on Account Progress.
const selection = useListSelectionStore('targets')
const { saving: bulkSaving, result: bulkResult, error: bulkError, run: runBulk } = useBulkAction(selection)
const bulkPanel = ref(null) // 'edit' | 'delete'
const pageKeys = computed(() => items.value.map(i => i.targetKey))
const keysUrl = computed(() => {
  const params = new URLSearchParams()
  if (typeFilter.value) params.set('targetTypeKey', typeFilter.value)
  if (applicationFilter.value) params.set('applicationKey', applicationFilter.value)
  if (linkedFilter.value !== 'Hide') params.set('linked', linkedFilter.value)
  return `/api/admin/targets/keys?${params.toString()}`
})
const bulkFields = computed(() => [
  { field: 'TargetTypeKey', label: t('pages.targets.fieldType'), type: 'select', required: true, options: targetTypes.value.map(type => ({ value: type.targetTypeKey, label: type.displayName })) },
  { field: 'ApplicationKey', label: t('pages.targets.fieldApplication'), type: 'select', options: applications.value.map(a => ({ value: a.applicationKey, label: a.applicationName })) },
  { field: 'RiskScore', label: t('pages.targets.fieldRiskScore'), type: 'number' },
  { field: 'Description', label: t('common.description'), type: 'text' }
])

async function bulkEdit(payload) {
  if (await runBulk('/api/admin/targets/bulk-edit', payload, t('pages.targets.verbUpdated'))) {
    bulkPanel.value = null
    await load()
  }
}

async function bulkDelete(reason) {
  if (await runBulk('/api/admin/targets/bulk-delete', { reason }, t('pages.targets.verbDeleted'))) {
    bulkPanel.value = null
    await load()
  }
}

async function remove(item) {
  // D-129/D-131: a structured Name/Scope/Address/Source summary (rendered
  // indented, set off from the title/warning) -- Target has no literal
  // Scope/Address field, so Scope shows its Target Type and Address shows
  // its first identifier value (a Target can have several; just the
  // first is shown, confirmed directly).
  const message = {
    title: t('pages.targets.confirmTitle'),
    details: [
      t('pages.targets.confirmName', { name: item.targetName }),
      t('pages.targets.confirmScope', { scope: item.targetTypeDisplayName }),
      t('pages.targets.confirmAddress', { address: item.identifiers[0]?.identifierValue ?? '—' }),
      t('pages.targets.confirmSource', { source: item.discoverySource ?? '—' })
    ],
    warning: t('pages.targets.confirmWarning')
  }
  if (!(await confirmDelete(message))) return
  const response = await fetch(`/api/admin/targets/${item.targetKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('pages.targets.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h1>{{ t('pages.targets.title') }}</h1>
    <p>{{ t('pages.targets.intro') }}</p>
    <p>
      <button type="button" class="btn-primary" @click="router.push({ name: 'target-create' })">{{ t('pages.targets.newTarget') }}</button>
      <button type="button" @click="router.push({ name: 'targets-bulk-import' })">{{ t('pages.targets.bulkActions') }}</button>
    </p>
    <p v-if="error" role="alert">{{ error }}</p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">{{ t('pages.targets.type') }}</span>
        <select v-model="typeFilter">
          <option value="">{{ t('pages.targets.all') }}</option>
          <option v-for="type in targetTypes" :key="type.targetTypeKey" :value="type.targetTypeKey">{{ type.displayName }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.targets.application') }}</span>
        <select v-model="applicationFilter">
          <option value="">{{ t('pages.targets.all') }}</option>
          <option v-for="app in applications" :key="app.applicationKey" :value="app.applicationKey">{{ app.applicationName }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.targets.linkedTargets') }}</span>
        <select v-model="linkedFilter">
          <option value="Hide">{{ t('pages.targets.hide') }}</option>
          <option value="Show">{{ t('pages.targets.show') }}</option>
        </select>
      </label>
    </p>
    <SelectionBar :selection="selection" :page-keys="pageKeys" :filtered-count="filteredCount" :keys-url="keysUrl" :toggle-label="t('pages.targets.selectToggle')">
      <button type="button" class="btn-primary" :disabled="selection.count === 0" @click="bulkPanel = 'edit'">{{ t('pages.targets.bulkEdit') }}</button>
      <button type="button" :disabled="selection.count === 0" @click="bulkPanel = 'delete'">{{ t('pages.targets.deleteEllipsis') }}</button>
    </SelectionBar>
    <BulkFieldEditor v-if="bulkPanel === 'edit'" :fields="bulkFields" :count="selection.count" :saving="bulkSaving" @apply="bulkEdit" @cancel="bulkPanel = null" />
    <BulkReasonForm v-if="bulkPanel === 'delete'" :action="t('pages.targets.deleteAction', { count: formatCount(selection.count) })" :button-label="t('common.delete')" :saving="bulkSaving" @submit="bulkDelete" @cancel="bulkPanel = null" />
    <p v-if="bulkError" role="alert">{{ bulkError }}</p>
    <BulkResultSummary v-if="bulkResult" :result="bulkResult" />
    <FilterCountSummary :shown="items.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />

    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <table>
        <thead>
          <tr>
            <th v-if="selection.enabled"><span class="visually-hidden">{{ t('pages.targets.selected') }}</span></th>
            <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
              <button type="button" @click="toggleSort(col.field, $event)">
                {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
              </button>
            </th>
            <th>{{ t('pages.targets.colIdentifiers') }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.targetKey">
            <td v-if="selection.enabled"><input type="checkbox" :checked="selection.isSelected(item.targetKey)" :aria-label="t('pages.targets.selectRow', { name: item.targetName })" @change="selection.toggle(item.targetKey)" /></td>
            <td>
              <router-link :to="{ name: 'target-edit', params: { targetKey: item.targetKey } }">{{ item.targetName }}</router-link>
              <router-link v-if="item.linkedToTargetKey" class="linked-badge" :to="{ name: 'target-edit', params: { targetKey: item.linkedToTargetKey } }"
                :title="t('pages.targets.linkedTitle', { name: item.linkedToTargetName })">{{ t('pages.targets.linkedBadge') }}<span class="visually-hidden">{{ t('pages.targets.linkedHidden', { name: item.linkedToTargetName }) }}</span></router-link>
            </td>
            <td>{{ item.targetTypeDisplayName }}</td>
            <td>{{ item.applicationName }}</td>
            <td>{{ item.riskScore }}</td>
            <td>{{ item.identifiers.map(i => `${i.identifierType}=${i.identifierValue}`).join(', ') }}</td>
            <td>
              <button @click="remove(item)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
      <p><small>{{ t('pages.targets.sortHint') }}</small></p>

      <h4>{{ t('pages.targets.linkHeading') }}</h4>
      <p v-if="linkError" role="alert">{{ linkError }}</p>
      <p v-if="linkSaved" role="status">{{ t('pages.targets.linked') }}</p>
      <form class="filter-row" @submit.prevent="createAccountTargetLink">
        <label class="field-label"><span class="field-label-text">{{ t('pages.targets.accountName') }}</span> <input v-model="linkAccountName" required /></label>
        <label class="field-label">
          <span class="field-label-text">{{ t('pages.targets.target') }}</span>
          <select v-model="linkTargetKey" required>
            <option :value="null" disabled>{{ t('pages.targets.choose') }}</option>
            <option v-for="item in items" :key="item.targetKey" :value="item.targetKey">{{ item.targetName }}</option>
          </select>
        </label>
        <button type="submit" class="btn-primary">{{ t('pages.targets.link') }}</button>
      </form>
    </template>
  </div>
</template>

<style scoped>
.linked-badge {
  margin-left: 0.5rem;
  font-size: 0.85em;
  opacity: 0.8;
}
</style>
