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
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useTotalCount } from '../../composables/useTotalCount'
import { usePageSizeStore } from '../../stores/pageSize'
import FilterCountSummary from '../../components/FilterCountSummary.vue'
import Pager from '../../components/Pager.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()
const pageSizeStore = usePageSizeStore()
const allApplications = ref([])
const applications = ref([])
const safes = ref([])
const error = ref(null)
const loading = ref(true)

const appCounts = useTotalCount()
const safeCounts = useTotalCount()
const appPage = ref(1)
const safePage = ref(1)
const pageCountFor = (count) => computed(() => Math.max(1, Math.ceil((count.value ?? 0) / pageSizeStore.current)))
const appPageCount = pageCountFor(appCounts.filteredCount)
const safePageCount = pageCountFor(safeCounts.filteredCount)

function pageParams(page) {
  return new URLSearchParams({ page, pageSize: pageSizeStore.current }).toString()
}

async function loadApplications() {
  const response = await fetch(`/api/applications/detailed?${pageParams(appPage.value)}`)
  if (!response.ok) throw new Error(t('admin.applicationSafeMapping.applicationsRequestFailed', { status: response.status }))
  appCounts.readTotalCount(response)
  applications.value = await response.json()
}

async function loadSafes() {
  const response = await fetch(`/api/safes?${pageParams(safePage.value)}`)
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
  const applicationKey = applicationKeyRaw === '' ? null : Number(applicationKeyRaw)
  const response = await fetch(`/api/safes/${safe.safeKey}/application`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(applicationKey)
  })
  if (!response.ok) {
    error.value = t('admin.applicationSafeMapping.assignFailed', { status: response.status })
    return
  }
  await run(loadSafes)
}
</script>

<template>
  <div>
    <h2>{{ t('admin.applicationSafeMapping.title') }}</h2>
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
          <tr><th>{{ t('admin.applicationSafeMapping.code') }}</th><th>{{ t('common.name') }}</th><th>{{ t('admin.applicationSafeMapping.owner') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="app in applications" :key="app.applicationKey">
            <td>{{ app.applicationCode }}</td>
            <td><router-link :to="{ name: 'admin-application-edit', params: { applicationKey: app.applicationKey } }">{{ app.applicationName }}</router-link></td>
            <td>{{ app.ownerName }}</td>
          </tr>
        </tbody>
      </table>
      <Pager :page="appPage" :page-count="appPageCount" @update:page="onAppPageChange" @page-size-change="onPageSizeChange" />

      <h3>{{ t('admin.applicationSafeMapping.safesHeading') }}</h3>
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
      <Pager :page="safePage" :page-count="safePageCount" @update:page="onSafePageChange" @page-size-change="onPageSizeChange" />
    </template>
  </div>
</template>
