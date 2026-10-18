<script setup>
// AD Account Discovery feature (2026-09-16): gated by ViewDiscoveredAccounts.
// Real AD accounts not yet onboarded into CyberArk, found nightly by
// matching AD group membership against the Access Group inventory, and
// risk-scored the same way any other Access-Group-reachable entity is.
// Sortable table + per-row drill-down mirror RiskScoreReport.vue's own
// shape exactly, since this is the same kind of "score plus what's behind
// it" report.
//
// Accept/Dismiss (requested directly, follow-up 2026-09-16) gated
// separately by ManageDiscoveredAccounts, since Accept writes a real
// dbo.fact_account/fact_account_progress row -- a bigger deal than viewing
// the report. Accept reuses the shared confirmDelete dialog (D-128) --
// despite the name, it's this app's one generic confirm-before-a-real-write
// dialog, already customizable via its confirmLabel param; Dismiss doesn't
// prompt (low-stakes, doesn't touch fact_account, and a dismissed row isn't
// deleted -- see GetListAsync's own Status filter).
//
// D-233: Account Class, Owner and Matched by columns from Admin > Account
// Classification's rules, a Class filter (sent to the export too), and a
// per-row Classify form (ManageDiscoveredAccounts) that sets or clears a
// manual class/owner override, kept across discovery runs.
import { ref, computed, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import { useTotalCount } from '../../composables/useTotalCount'
import { useLatestRequest } from '../../composables/useLatestRequest'
import { usePageSizeStore } from '../../stores/pageSize'
import { useRightsStore } from '../../stores/rights'
import { confirmDelete } from '../../composables/useConfirmDialog'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const latestRequest = useLatestRequest()
const pageSizeStore = usePageSizeStore()
const rights = useRightsStore()

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

const rows = ref([])
const error = ref(null)
const loading = ref(true)

const expandedKey = ref(null)
const accessGroups = ref([])
const accessGroupsLoading = ref(false)
const accessGroupsError = ref(null)

const sortColumns = ref([])

const columns = [
  { field: 'samAccountName', labelKey: 'reports.discoveredAccounts.colAccount' },
  { field: 'domainName', labelKey: 'reports.discoveredAccounts.colDomain' },
  { field: 'accountClass', labelKey: 'reports.discoveredAccounts.colAccountClass' },
  { field: 'ownerSamAccountName', labelKey: 'reports.discoveredAccounts.colOwner' },
  { field: 'computedRiskScore', labelKey: 'reports.discoveredAccounts.colRiskScore' },
  { field: 'riskScoreBandName', labelKey: 'reports.discoveredAccounts.colRiskBand' },
  { field: 'discoveredDate', labelKey: 'reports.discoveredAccounts.colFirstDiscovered' },
  { field: 'lastSeenDate', labelKey: 'reports.discoveredAccounts.colLastSeen' }
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
      sortColumns.value = [{ field, descending: field === 'computedRiskScore' }]
    }
    page.value = 1
    load()
    return
  }

  if (existingIndex === -1) {
    sortColumns.value = [...sortColumns.value, { field, descending: false }]
  } else {
    const updated = [...sortColumns.value]
    updated[existingIndex] = { ...updated[existingIndex], descending: !updated[existingIndex].descending }
    sortColumns.value = updated
  }
  page.value = 1
  load()
}

const sortQueryParam = computed(() =>
  sortColumns.value.map(s => `${s.field}:${s.descending ? 'desc' : 'asc'}`).join(','))

const classFilter = ref('')
const columnCount = computed(() => columns.length + 1 + (rights.hasPermission('ManageDiscoveredAccounts') ? 1 : 0))

const CLASS_KEYS = {
  Primary: 'reports.discoveredAccounts.classPrimary',
  Secondary: 'reports.discoveredAccounts.classSecondary',
  Admin: 'reports.discoveredAccounts.classAdmin',
  Unknown: 'reports.discoveredAccounts.classUnknown'
}
const CLASSES = ['Primary', 'Secondary', 'Admin', 'Unknown']

const OPERATOR_KEYS = {
  Equals: 'reports.discoveredAccounts.opEquals',
  Contains: 'reports.discoveredAccounts.opContains',
  StartsWith: 'reports.discoveredAccounts.opStartsWith',
  EndsWith: 'reports.discoveredAccounts.opEndsWith',
  IsSet: 'reports.discoveredAccounts.opIsSet',
  Pattern: 'reports.discoveredAccounts.opPattern'
}

const OWNER_SOURCE_KEYS = {
  Name: 'reports.discoveredAccounts.ownerByName',
  EmployeeId: 'reports.discoveredAccounts.ownerByEmployeeId',
  Manual: 'reports.discoveredAccounts.ownerManual',
  Ambiguous: 'reports.discoveredAccounts.ownerAmbiguous'
}

function classLabel(value) {
  return value ? t(CLASS_KEYS[value]) : '—'
}

function matchedBy(row) {
  if (row.classSource === 'Manual') return t('reports.discoveredAccounts.matchedManual', { by: row.overrideByName ?? '' })
  if (row.classSource === 'Default') return t('reports.discoveredAccounts.matchedDefault')
  if (row.classSource === 'Rule' && row.classRuleType) {
    const subject = row.classRuleType === 'OU' ? t('reports.discoveredAccounts.distinguishedName') : row.classRuleAttributeName
    if (row.classRuleOperator === 'IsSet') return t('reports.discoveredAccounts.matchedRuleIsSet', { subject })
    return t('reports.discoveredAccounts.matchedRule', { subject, operator: t(OPERATOR_KEYS[row.classRuleOperator]), value: row.classRuleValue })
  }
  if (row.classSource === 'Rule') return t('reports.discoveredAccounts.matchedRemovedRule')
  return ''
}

function onClassFilterChange() {
  page.value = 1
  load()
}

const editingKey = ref(null)
const overrideForm = ref({ accountClass: '', ownerSamAccountName: '' })
const overrideError = ref(null)

function startClassify(row) {
  editingKey.value = row.discoveredAccountKey
  overrideError.value = null
  overrideForm.value = { accountClass: row.classOverride ?? '', ownerSamAccountName: row.ownerOverrideSamAccountName ?? '' }
}

async function saveOverride(row, clear = false) {
  overrideError.value = null
  busyKey.value = row.discoveredAccountKey
  try {
    const body = clear
      ? { accountClass: null, ownerSamAccountName: null }
      : { accountClass: overrideForm.value.accountClass || null, ownerSamAccountName: overrideForm.value.ownerSamAccountName.trim() || null }
    const response = await fetch(`/api/reports/discovered-accounts/${row.discoveredAccountKey}/classification`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail ?? t('common.requestFailed', { status: response.status }))
    editingKey.value = null
    await load()
  } catch (err) {
    overrideError.value = err.message
  } finally {
    busyKey.value = null
  }
}

async function load() {
  const request = latestRequest.begin()
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    if (classFilter.value) params.set('accountClass', classFilter.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)
    const response = await fetch(`/api/reports/discovered-accounts?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    const body = await response.json()
    if (!latestRequest.isCurrent(request)) return
    readTotalCount(response)
    rows.value = body
  } catch (err) {
    if (latestRequest.isCurrent(request)) error.value = err.message
  } finally {
    if (latestRequest.isCurrent(request)) loading.value = false
  }
}

const actionError = ref(null)
const busyKey = ref(null)

async function accept(row) {
  if (!(await confirmDelete(
    t('reports.discoveredAccounts.acceptConfirm', { account: row.samAccountName, domain: row.domainName }),
    t('reports.discoveredAccounts.accept')
  ))) return

  actionError.value = null
  busyKey.value = row.discoveredAccountKey
  try {
    const response = await fetch(`/api/reports/discovered-accounts/${row.discoveredAccountKey}/accept`, { method: 'POST' })
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail ?? t('common.requestFailed', { status: response.status }))
    await load()
  } catch (err) {
    actionError.value = err.message
  } finally {
    busyKey.value = null
  }
}

async function dismiss(row) {
  actionError.value = null
  busyKey.value = row.discoveredAccountKey
  try {
    const response = await fetch(`/api/reports/discovered-accounts/${row.discoveredAccountKey}/dismiss`, { method: 'POST' })
    if (!response.ok) throw new Error((await response.json().catch(() => null))?.detail ?? t('common.requestFailed', { status: response.status }))
    await load()
  } catch (err) {
    actionError.value = err.message
  } finally {
    busyKey.value = null
  }
}

async function toggleDrilldown(discoveredAccountKey) {
  if (expandedKey.value === discoveredAccountKey) {
    expandedKey.value = null
    return
  }

  expandedKey.value = discoveredAccountKey
  accessGroups.value = []
  accessGroupsError.value = null
  accessGroupsLoading.value = true
  try {
    const response = await fetch(`/api/reports/discovered-accounts/${discoveredAccountKey}/access-groups`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    accessGroups.value = await response.json()
  } catch (err) {
    accessGroupsError.value = err.message
  } finally {
    accessGroupsLoading.value = false
  }
}

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('reports.discoveredAccounts.title') }}</h2>
    <p class="hint">{{ t('reports.discoveredAccounts.hint') }}</p>
    <p>
      <label>{{ t('reports.discoveredAccounts.classFilter') }}
        <select v-model="classFilter" @change="onClassFilterChange">
          <option value="">{{ t('reports.discoveredAccounts.classFilterAll') }}</option>
          <option v-for="c in CLASSES" :key="c" :value="c">{{ t(CLASS_KEYS[c]) }}</option>
        </select>
      </label>
    </p>
    <ReportExportButtons path="/api/reports/discovered-accounts/export" :params="{ sort: sortQueryParam, accountClass: classFilter || null }" />
    <FilterCountSummary :shown="rows.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="actionError" role="alert">{{ actionError }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.discoveredAccounts.loadFailed', { error }) }}</p>
    <table v-else>
      <thead>
        <tr>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
          <th>{{ t('reports.discoveredAccounts.colMatchedBy') }}</th>
          <th v-if="rights.hasPermission('ManageDiscoveredAccounts')"></th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.discoveredAccountKey">
          <tr>
            <td>
              <button type="button" class="link-button" @click="toggleDrilldown(row.discoveredAccountKey)">
                {{ row.samAccountName }}
              </button>
              <span v-if="row.displayName"> ({{ row.displayName }})</span>
              <span v-if="!row.isEnabled">{{ ' ' + t('reports.discoveredAccounts.disabled') }}</span>
              <p v-if="row.possibleExistingAccountKey" class="possible-match" role="status">
                {{ t('reports.discoveredAccounts.possibleMatch', { account: row.possibleExistingAccountName }) }}
              </p>
            </td>
            <td>{{ row.domainName }}</td>
            <td>{{ classLabel(row.accountClass) }}</td>
            <td>
              <template v-if="row.ownerSamAccountName">
                {{ row.ownerSamAccountName }}<span v-if="row.ownerDisplayName"> ({{ row.ownerDisplayName }})</span>
              </template>
              <template v-else>{{ '—' }}</template>
            </td>
            <td>
              {{ matchedBy(row) }}
              <span v-if="OWNER_SOURCE_KEYS[row.ownerSource]" class="owner-source">{{ t(OWNER_SOURCE_KEYS[row.ownerSource]) }}</span>
            </td>
            <td>{{ row.computedRiskScore ?? '—' }}</td>
            <td>{{ row.riskScoreBandName ?? '—' }}</td>
            <td>{{ formatDate(row.discoveredDate) }}</td>
            <td>{{ formatDate(row.lastSeenDate) }}</td>
            <td v-if="rights.hasPermission('ManageDiscoveredAccounts')">
              <button type="button" :disabled="busyKey === row.discoveredAccountKey" @click="accept(row)">{{ t('reports.discoveredAccounts.accept') }}</button>
              <button type="button" :disabled="busyKey === row.discoveredAccountKey" @click="dismiss(row)">{{ t('reports.discoveredAccounts.dismiss') }}</button>
              <button type="button" :disabled="busyKey === row.discoveredAccountKey" :aria-label="t('reports.discoveredAccounts.classifyLabel', { account: row.samAccountName })" @click="startClassify(row)">{{ t('reports.discoveredAccounts.classify') }}</button>
            </td>
          </tr>
          <tr v-if="editingKey === row.discoveredAccountKey">
            <td :colspan="columnCount">
              <form class="override-form" @submit.prevent="saveOverride(row)">
                <h3>{{ t('reports.discoveredAccounts.classifyHeading', { account: row.samAccountName }) }}</h3>
                <label>{{ t('reports.discoveredAccounts.overrideClass') }}
                  <select v-model="overrideForm.accountClass">
                    <option value="">{{ t('reports.discoveredAccounts.overrideFollowRules') }}</option>
                    <option v-for="c in CLASSES" :key="c" :value="c">{{ t(CLASS_KEYS[c]) }}</option>
                  </select>
                </label>
                <label>{{ t('reports.discoveredAccounts.overrideOwner') }}
                  <input v-model="overrideForm.ownerSamAccountName" maxlength="300" />
                </label>
                <p class="hint">{{ t('reports.discoveredAccounts.overrideHint') }}</p>
                <p v-if="overrideError" role="alert">{{ overrideError }}</p>
                <button type="submit" class="btn-primary" :disabled="busyKey === row.discoveredAccountKey">{{ t('common.save') }}</button>
                <button v-if="row.classOverride || row.ownerOverrideSamAccountName" type="button" :disabled="busyKey === row.discoveredAccountKey" @click="saveOverride(row, true)">{{ t('reports.discoveredAccounts.clearOverride') }}</button>
                <button type="button" @click="editingKey = null">{{ t('common.cancel') }}</button>
              </form>
            </td>
          </tr>
          <tr v-if="expandedKey === row.discoveredAccountKey">
            <td :colspan="columnCount">
              <p v-if="accessGroupsLoading" role="status">{{ t('reports.discoveredAccounts.loadingAccessGroups') }}</p>
              <p v-else-if="accessGroupsError" role="alert">{{ accessGroupsError }}</p>
              <p v-else-if="accessGroups.length === 0">{{ t('reports.discoveredAccounts.noAccessGroups') }}</p>
              <ul v-else class="drilldown-list">
                <li v-for="g in accessGroups" :key="g.accessGroupKey">{{ g.groupName }}</li>
              </ul>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p><small>{{ t('reports.discoveredAccounts.sortHelp') }}</small></p>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}

.link-button {
  background: none;
  border: none;
  padding: 0;
  color: var(--color-link);
  text-decoration: underline;
  cursor: pointer;
  font: inherit;
}

.possible-match {
  margin: var(--space-1) 0 0;
  font-size: 0.875em;
}

.owner-source {
  display: block;
  font-size: 0.875em;
}

.override-form label {
  margin-right: 0.5rem;
}

.override-form h3 {
  margin-top: 0;
  font-size: 1em;
}

.drilldown-list {
  margin: 0.5rem 0 0.5rem 1.5rem;
}
</style>
