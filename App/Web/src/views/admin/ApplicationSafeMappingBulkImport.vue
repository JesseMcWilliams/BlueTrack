<script setup>
import { apiUrl } from '../../utils/apiBase'
// D-180: CSV imports for the Application ↔ Safe Mapping page, reached via
// its "Bulk Actions" button -- the same upload / Import / result-summary
// pattern as TargetsBulkImport.vue (D-124 Phase 5). Two separate imports:
// Applications first (create/update), then Safe -> Application assignments,
// which can only name applications that already exist.
import { ref } from 'vue'
import { formatCount } from '../../utils/formatCount'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const applicationsFile = ref(null)
const applicationsResult = ref(null)
const applicationsImporting = ref(false)

const assignmentsFile = ref(null)
const assignmentsResult = ref(null)
const assignmentsImporting = ref(false)

const accountAssignmentsFile = ref(null)
const accountAssignmentsResult = ref(null)
const accountAssignmentsImporting = ref(false)

async function importFile(url, fileRef, resultRef, importingRef) {
  if (!fileRef.value) return
  importingRef.value = true
  resultRef.value = null
  try {
    const formData = new FormData()
    formData.append('file', fileRef.value)
    const response = await fetch(url, { method: 'POST', body: formData })
    resultRef.value = response.ok ? await response.json() : { error: t('common.importFailed', { status: response.status }) }
  } finally {
    importingRef.value = false
  }
}

function rowErrors(result) {
  return result.errors.map(e => t('admin.applicationSafeMappingBulkImport.rowError', { row: e.rowNumber, error: e.error })).join('; ')
}

async function importApplications() {
  await importFile('/api/admin/application-mapping/import/applications', applicationsFile, applicationsResult, applicationsImporting)
}

async function importAccountAssignments() {
  await importFile('/api/admin/application-mapping/import/account-assignments', accountAssignmentsFile, accountAssignmentsResult, accountAssignmentsImporting)
}

async function importAssignments() {
  await importFile('/api/admin/application-mapping/import/safe-assignments', assignmentsFile, assignmentsResult, assignmentsImporting)
}
</script>

<template>
  <div>
    <h1>{{ t('admin.applicationSafeMappingBulkImport.title') }}</h1>

    <h3>{{ t('admin.applicationSafeMappingBulkImport.applicationsHeading') }}</h3>
    <p>
      <a :href="apiUrl('/api/admin/application-mapping/import/applications/template')">{{ t('common.downloadTemplate') }}</a> {{ t('admin.applicationSafeMappingBulkImport.applicationsIntro') }}
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('admin.applicationSafeMappingBulkImport.applicationsFileLabel')" @change="applicationsFile = $event.target.files[0] ?? null" />
      <button :disabled="!applicationsFile || applicationsImporting" @click="importApplications">{{ applicationsImporting ? t('common.importing') : t('admin.applicationSafeMappingBulkImport.import') }}</button>
    </p>
    <p v-if="applicationsResult" data-testid="applications-result">
      <template v-if="applicationsResult.error">{{ applicationsResult.error }}</template>
      <template v-else>
        {{ t('admin.applicationSafeMappingBulkImport.applicationsSummary', { total: formatCount(applicationsResult.totalRows), created: formatCount(applicationsResult.createdCount), updated: formatCount(applicationsResult.updatedCount), unchanged: formatCount(applicationsResult.unchangedCount), errors: formatCount(applicationsResult.errors.length) }) }}
        <span v-if="applicationsResult.errors.length"><br />{{ rowErrors(applicationsResult) }}</span>
      </template>
    </p>

    <h3>{{ t('admin.applicationSafeMappingBulkImport.assignmentsHeading') }}</h3>
    <p>
      <a :href="apiUrl('/api/admin/application-mapping/import/safe-assignments/template')">{{ t('common.downloadTemplate') }}</a> {{ t('admin.applicationSafeMappingBulkImport.assignmentsIntro') }}
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('admin.applicationSafeMappingBulkImport.assignmentsFileLabel')" @change="assignmentsFile = $event.target.files[0] ?? null" />
      <button :disabled="!assignmentsFile || assignmentsImporting" @click="importAssignments">{{ assignmentsImporting ? t('common.importing') : t('admin.applicationSafeMappingBulkImport.import') }}</button>
    </p>
    <p v-if="assignmentsResult" data-testid="assignments-result">
      <template v-if="assignmentsResult.error">{{ assignmentsResult.error }}</template>
      <template v-else>
        {{ t('admin.applicationSafeMappingBulkImport.assignmentsSummary', { total: formatCount(assignmentsResult.totalRows), assigned: formatCount(assignmentsResult.assignedCount), changed: formatCount(assignmentsResult.changedCount), unchanged: formatCount(assignmentsResult.unchangedCount), errors: formatCount(assignmentsResult.errors.length) }) }}
        <span v-if="assignmentsResult.errors.length"><br />{{ rowErrors(assignmentsResult) }}</span>
      </template>
    </p>

    <h3>{{ t('admin.applicationSafeMappingBulkImport.accountAssignmentsHeading') }}</h3>
    <p>
      <a :href="apiUrl('/api/admin/application-mapping/import/account-assignments/template')">{{ t('common.downloadTemplate') }}</a> {{ t('admin.applicationSafeMappingBulkImport.accountAssignmentsIntro') }}
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('admin.applicationSafeMappingBulkImport.accountAssignmentsFileLabel')" @change="accountAssignmentsFile = $event.target.files[0] ?? null" />
      <button :disabled="!accountAssignmentsFile || accountAssignmentsImporting" @click="importAccountAssignments">{{ accountAssignmentsImporting ? t('common.importing') : t('admin.applicationSafeMappingBulkImport.import') }}</button>
    </p>
    <p v-if="accountAssignmentsResult" data-testid="account-assignments-result">
      <template v-if="accountAssignmentsResult.error">{{ accountAssignmentsResult.error }}</template>
      <template v-else>
        {{ t('admin.applicationSafeMappingBulkImport.accountAssignmentsSummary', { total: formatCount(accountAssignmentsResult.totalRows), assigned: formatCount(accountAssignmentsResult.assignedCount), changed: formatCount(accountAssignmentsResult.changedCount), unchanged: formatCount(accountAssignmentsResult.unchangedCount), errors: formatCount(accountAssignmentsResult.errors.length) }) }}
        <span v-if="accountAssignmentsResult.errors.length"><br />{{ rowErrors(accountAssignmentsResult) }}</span>
      </template>
    </p>
  </div>
</template>
