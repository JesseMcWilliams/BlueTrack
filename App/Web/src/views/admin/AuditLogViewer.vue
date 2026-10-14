<script setup>
// Searchable/filterable view against /api/audit-log (AuditLogController) --
// filters map to query params, and a click on a row drills into its
// field-level changes (Design_Audit-Logging.md's Admin UI Requirements).
import { ref, computed, watch, onMounted } from 'vue'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const { totalCount, filteredCount, readTotalCount } = useTotalCount()
const pageSizeStore = usePageSizeStore()
const events = ref([])
const error = ref(null)
const loading = ref(true)

const eventTypeFilter = ref('')
const entityNameFilter = ref('')
const fromDateFilter = ref('')
const toDateFilter = ref('')

// D-231: the filters last submitted (Filter button). The grid's paging/sort
// reloads and the Export buttons both use these, never unsubmitted form edits.
const appliedFilters = ref({ eventType: '', entityName: '', fromDate: '', toDate: '' })

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

// D-124 Phase 3: the filter form here submits explicitly (@submit.prevent
// ="loadFiltered" below), unlike the other five pages' watch-driven
// auto-reload -- still needs the same page-1 reset on every new filter
// submission.
function loadFiltered() {
  appliedFilters.value = {
    eventType: eventTypeFilter.value,
    entityName: entityNameFilter.value,
    fromDate: fromDateFilter.value,
    toDate: toDateFilter.value
  }
  page.value = 1
  load()
}

const expandedEventKey = ref(null)
const fieldChanges = ref([])

// D-42: multi-column sort, same click/shift-click pattern as Account
// Progress List and the Risk Exceptions list.
const sortColumns = ref([])
const columns = [
  { field: 'occurredAt', labelKey: 'admin.auditLogViewer.occurredAt' },
  { field: 'eventTypeName', labelKey: 'admin.auditLogViewer.eventType' },
  { field: 'performedByName', labelKey: 'admin.auditLogViewer.by' },
  { field: 'entityName', labelKey: 'admin.auditLogViewer.entity' }
]

function sortIndicator(field) {
  const idx = sortColumns.value.findIndex(s => s.field === field)
  if (idx === -1) return ''
  const arrow = sortColumns.value[idx].descending ? '▼' : '▲'
  return sortColumns.value.length > 1 ? `${arrow}${idx + 1}` : arrow
}

// D-92 (ARIA APG Sortable Table pattern) -- see AccountProgressList.vue's
// identical helper for why only the primary sort key is ever reflected here.
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

// D-231: what Export CSV / Export Excel send -- the applied filters and the current sort.
const exportParams = computed(() => ({ ...appliedFilters.value, sort: sortQueryParam.value }))

async function load() {
  loading.value = true
  error.value = null
  try {
    const params = new URLSearchParams()
    const applied = appliedFilters.value
    if (applied.eventType) params.set('eventType', applied.eventType)
    if (applied.entityName) params.set('entityName', applied.entityName)
    if (applied.fromDate) params.set('fromDate', applied.fromDate)
    if (applied.toDate) params.set('toDate', applied.toDate)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)
    params.set('page', page.value)
    params.set('pageSize', pageSizeStore.current)

    const response = await fetch(`/api/audit-log?${params.toString()}`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    readTotalCount(response)
    events.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
// D-124 Phase 3: a sort change resets to page 1 -- see Targets.vue's
// identical comment for why page-size/Prev/Next changes are handled separately.
watch(sortQueryParam, () => {
  page.value = 1
  load()
})

async function toggleFieldChanges(event) {
  if (expandedEventKey.value === event.auditEventKey) {
    expandedEventKey.value = null
    return
  }
  const response = await fetch(`/api/audit-log/${event.auditEventKey}/field-changes`)
  fieldChanges.value = response.ok ? await response.json() : []
  expandedEventKey.value = event.auditEventKey
}
</script>

<template>
  <div>
    <h2>{{ t('admin.auditLogViewer.title') }}</h2>

    <form class="filter-row" @submit.prevent="loadFiltered">
      <label class="field-label"><span class="field-label-text">{{ t('admin.auditLogViewer.eventTypeLabel') }}</span> <input v-model="eventTypeFilter" :placeholder="t('admin.auditLogViewer.eventTypePlaceholder')" /></label>
      <label class="field-label"><span class="field-label-text">{{ t('admin.auditLogViewer.entityLabel') }}</span> <input v-model="entityNameFilter" :placeholder="t('admin.auditLogViewer.entityPlaceholder')" /></label>
      <label class="field-label"><span class="field-label-text">{{ t('admin.auditLogViewer.fromLabel') }}</span> <input v-model="fromDateFilter" type="date" /></label>
      <label class="field-label"><span class="field-label-text">{{ t('admin.auditLogViewer.toLabel') }}</span> <input v-model="toDateFilter" type="date" /></label>
      <button type="submit" class="btn-primary">{{ t('admin.auditLogViewer.filter') }}</button>
    </form>

    <ReportExportButtons path="/api/audit-log/export" :params="exportParams" />
    <FilterCountSummary :shown="events.length" :filtered-count="filteredCount" :total="totalCount" :page="page" :page-size="pageSizeStore.current" />
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="events.length === 0">{{ t('admin.auditLogViewer.noEvents') }}</p>

    <table v-else>
      <thead>
        <tr>
          <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
            <button type="button" @click="toggleSort(col.field, $event)">
              {{ t(col.labelKey) }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
            </button>
          </th>
          <th>{{ t('admin.auditLogViewer.detail') }}</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="event in events" :key="event.auditEventKey">
          <tr>
            <td>
              <button
                type="button"
                :aria-expanded="expandedEventKey === event.auditEventKey"
                :aria-controls="`audit-detail-${event.auditEventKey}`"
                @click="toggleFieldChanges(event)"
              >
                {{ event.occurredAt }}
              </button>
            </td>
            <td>{{ event.eventTypeName }}</td>
            <td>{{ event.performedByName }}</td>
            <td>{{ event.entityName }} {{ event.entityKey }}</td>
            <td>{{ event.detail }}</td>
          </tr>
          <tr v-if="expandedEventKey === event.auditEventKey" :id="`audit-detail-${event.auditEventKey}`">
            <td colspan="5">
              <p v-if="event.reason"><strong>{{ t('admin.auditLogViewer.reason') }}</strong> {{ event.reason }}</p>
              <p v-if="fieldChanges.length === 0">{{ t('admin.auditLogViewer.noFieldChanges') }}</p>
              <table v-else>
                <thead><tr><th>{{ t('admin.auditLogViewer.field') }}</th><th>{{ t('admin.auditLogViewer.oldValue') }}</th><th>{{ t('admin.auditLogViewer.newValue') }}</th></tr></thead>
                <tbody>
                  <tr v-for="change in fieldChanges" :key="change.fieldName">
                    <td>{{ change.fieldName }}</td>
                    <td>{{ change.oldValue }}</td>
                    <td>{{ change.newValue }}</td>
                  </tr>
                </tbody>
              </table>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
    <Pager :page="page" :page-count="pageCount" @update:page="onPageChange" @page-size-change="onPageSizeChange" />
  </div>
</template>
