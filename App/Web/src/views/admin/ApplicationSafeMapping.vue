<script setup>
// Application CRUD against /api/applications (detailed/create/update) plus
// Safe -> Application assignment against /api/safes (SafesController).
//
// D-124 Phase 4: the Applications section's Add/Edit moved to its own
// routed page (ApplicationEdit.vue, admin-application-create/-edit) -- this
// page no longer owns an inline editing/startCreate/startEdit/cancelEdit
// form for Applications. The Safes section below is a plain per-row
// <select> assignment, not a form.
//
// D-213: both tables page server-side, like Accounts, at the user's page
// size (the shared pageSize store). The Safes rows' <select> still lists
// every application, so the full list is loaded once for it.
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { problemMessage } from '../../utils/problemText'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()

const router = useRouter()
const pageSizeStore = usePageSizeStore()
const allApplications = ref([])
const applications = ref([])
const safes = ref([])
const error = ref(null)
const loading = ref(true)
// UI review W8-1: an assignment is saved the moment the select changes, so its outcome shows next to the Safes table
// (the page-level `error` sits at the top, off-screen on a long list, and is cleared by every reload).
const assignError = ref(null)
const assignNotice = ref(null)

const appCounts = useTotalCount()
const safeCounts = useTotalCount()
const appPage = ref(1)
const safePage = ref(1)
// UI review W8-2: the Safes table can be searched by name and narrowed to Safes with no application.
const safeSearch = ref('')
const unassignedOnly = ref(false)
const pageCountFor = (count) => computed(() => Math.max(1, Math.ceil((count.value ?? 0) / pageSizeStore.current)))
const appPageCount = pageCountFor(appCounts.filteredCount)
const safePageCount = pageCountFor(safeCounts.filteredCount)

function pageParams(page, extra = {}) {
  return new URLSearchParams({ page, pageSize: pageSizeStore.current, ...extra }).toString()
}

function safeFilterParams() {
  const extra = {}
  if (safeSearch.value.trim()) extra.search = safeSearch.value.trim()
  if (unassignedOnly.value) extra.unassigned = 'true'
  return extra
}

async function loadApplications() {
  const response = await fetch(`/api/applications/detailed?${pageParams(appPage.value)}`)
  if (!response.ok) throw new Error(t('admin.applicationSafeMapping.applicationsRequestFailed', { status: response.status }))
  appCounts.readTotalCount(response)
  applications.value = await response.json()
}

async function loadSafes() {
  const response = await fetch(`/api/safes?${pageParams(safePage.value, safeFilterParams())}`)
  if (!response.ok) throw new Error(t('admin.applicationSafeMapping.safesRequestFailed', { status: response.status }))
  safeCounts.readTotalCount(response)
  safes.value = await response.json()
}

async function loadAllApplications() {
  const response = await fetch('/api/applications/detailed')
  if (!response.ok) throw new Error(t('admin.applicationSafeMapping.applicationsRequestFailed', { status: response.status }))
  allApplications.value = await response.json()
}

async function run(work) {
  error.value = null
  try {
    await work()
  } catch (err) {
    error.value = err.message
  }
}

onMounted(async () => {
  await run(() => Promise.all([loadApplications(), loadSafes(), loadAllApplications()]))
  loading.value = false
})

// A change to the search or the unassigned filter starts the Safes table over at page 1, after a short pause for typing.
let safeFilterTimer = null
watch([safeSearch, unassignedOnly], () => {
  clearTimeout(safeFilterTimer)
  safeFilterTimer = setTimeout(() => {
    safePage.value = 1
    run(loadSafes)
  }, 250)
})

// UI review W8-5: delete an application nothing uses (the API refuses one that is in use, with the counts).
const usedBy = (app) => app.safeCount + app.accountCount + app.targetCount + app.exceptionCount
async function removeApplication(app) {
  if (!(await confirmDelete(t('admin.applicationSafeMapping.confirmDelete', { name: app.applicationName })))) return
  const response = await fetch(`/api/applications/${app.applicationKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = await problemMessage(response, t('common.deleteFailed', { status: response.status }))
    return
  }
  error.value = null
  await run(() => Promise.all([loadApplications(), loadSafes(), loadAllApplications()]))
}

function onAppPageChange(page) {
  appPage.value = page
  run(loadApplications)
}

function onSafePageChange(page) {
  safePage.value = page
  run(loadSafes)
}

// The page size is shared, so a change from either pager restarts both tables at page 1.
function onPageSizeChange() {
  appPage.value = 1
  safePage.value = 1
  run(() => Promise.all([loadApplications(), loadSafes()]))
}

async function assignSafe(safe, applicationKeyRaw) {
  assignError.value = null
  assignNotice.value = null
  const applicationKey = applicationKeyRaw === '' ? null : Number(applicationKeyRaw)
  const applicationName = allApplications.value.find(a => a.applicationKey === applicationKey)?.applicationName
  let response = null
  try {
    response = await fetch(`/api/safes/${safe.safeKey}/application`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(applicationKey)
    })
  } catch {
    // no response at all: reported below
  }
  if (!response?.ok) {
    const problem = response ? await response.json().catch(() => null) : null
    assignError.value = t('admin.applicationSafeMapping.assignFailedFor', {
      name: safe.safeName,
      reason: problem?.detail ?? (response ? t('common.requestFailed', { status: response.status }) : t('admin.applicationSafeMapping.noResponse'))
    })
    // The select still shows what was picked; reload so it goes back to what is saved.
    await run(loadSafes)
    return
  }
  assignNotice.value = applicationName
    ? t('admin.applicationSafeMapping.assignedTo', { name: safe.safeName, application: applicationName })
    : t('admin.applicationSafeMapping.unassigned', { name: safe.safeName })
  await run(loadSafes)
}
</script>

<template>
  <div>
    <h2>{{ t('admin.applicationSafeMapping.title') }}</h2>
    <!-- UI review W8-6: where an account's application comes from. -->
    <p class="intro">{{ t('admin.applicationSafeMapping.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <h3>{{ t('admin.applicationSafeMapping.applicationsHeading') }}</h3>
      <p>
        <button type="button" class="btn-primary" @click="router.push({ name: 'admin-application-create' })">{{ t('admin.applicationSafeMapping.newApplication') }}</button>
        <button type="button" @click="router.push({ name: 'admin-application-mapping-bulk-import' })">{{ t('admin.applicationSafeMapping.bulkActions') }}</button>
      </p>
      <FilterCountSummary :shown="applications.length" :filtered-count="appCounts.filteredCount.value" :total="appCounts.totalCount.value" :page="appPage" :page-size="pageSizeStore.current" />
      <table class="applications-table">
        <thead>
          <tr><th>{{ t('admin.applicationSafeMapping.code') }}</th><th>{{ t('common.name') }}</th><th>{{ t('admin.applicationSafeMapping.owner') }}</th><th>{{ t('admin.applicationSafeMapping.usedBy') }}</th><th><span class="visually-hidden">{{ t('admin.applicationSafeMapping.actions') }}</span></th></tr>
        </thead>
        <tbody>
          <tr v-for="app in applications" :key="app.applicationKey">
            <td>{{ app.applicationCode }}</td>
            <td><router-link :to="{ name: 'admin-application-edit', params: { applicationKey: app.applicationKey } }">{{ app.applicationName }}</router-link></td>
            <td>{{ app.ownerName }}</td>
            <td>{{ t('admin.applicationSafeMapping.usage', { safes: formatCount(app.safeCount), accounts: formatCount(app.accountCount), targets: formatCount(app.targetCount), exceptions: formatCount(app.exceptionCount) }) }}</td>
            <td><button type="button" :disabled="usedBy(app) > 0" :title="usedBy(app) > 0 ? t('admin.applicationSafeMapping.inUse') : undefined" :aria-label="t('common.deleteNamed', { name: app.applicationName })" @click="removeApplication(app)">{{ t('common.delete') }}</button></td>
          </tr>
        </tbody>
      </table>
      <Pager :page="appPage" :page-count="appPageCount" @update:page="onAppPageChange" @page-size-change="onPageSizeChange" />

      <h3>{{ t('admin.applicationSafeMapping.safesHeading') }}</h3>
      <p class="filter-row">
        <label class="field-label"><span class="field-label-text">{{ t('admin.applicationSafeMapping.searchSafes') }}</span> <input v-model="safeSearch" type="search" /></label>
        <label><input v-model="unassignedOnly" type="checkbox" /> {{ t('admin.applicationSafeMapping.unassignedOnly') }}</label>
      </p>
      <p v-if="assignError" role="alert" class="assign-error">{{ assignError }}</p>
      <p v-if="assignNotice" role="status" class="assign-notice">{{ assignNotice }}</p>
      <FilterCountSummary :shown="safes.length" :filtered-count="safeCounts.filteredCount.value" :total="safeCounts.totalCount.value" :page="safePage" :page-size="pageSizeStore.current" />
      <table class="safes-table">
        <thead>
          <tr><th>{{ t('admin.applicationSafeMapping.safe') }}</th><th>{{ t('admin.applicationSafeMapping.application') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="safe in safes" :key="safe.safeKey">
            <td>{{ safe.safeName }}</td>
            <td>
              <select :value="safe.applicationKey ?? ''" :aria-label="t('admin.applicationSafeMapping.applicationForSafe', { name: safe.safeName })" @change="assignSafe(safe, $event.target.value)">
                <option value="">{{ t('common.none') }}</option>
                <option v-for="app in allApplications" :key="app.applicationKey" :value="app.applicationKey">
                  {{ app.applicationName }}
                </option>
              </select>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="safes.length === 0" :message="t('admin.applicationSafeMapping.noSafesMatch')" />
      <Pager :page="safePage" :page-count="safePageCount" @update:page="onSafePageChange" @page-size-change="onPageSizeChange" />
    </template>
  </div>
</template>
