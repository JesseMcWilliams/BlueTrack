<script setup>
// D-180: CSV imports for the Application ↔ Safe Mapping page, reached via
// its "Bulk Actions" button -- the same upload / Import / result-summary
// pattern as TargetsBulkImport.vue (D-124 Phase 5). Two separate imports:
// Applications first (create/update), then Safe -> Application assignments,
// which can only name applications that already exist.
import { ref } from 'vue'

const applicationsFile = ref(null)
const applicationsResult = ref(null)
const applicationsImporting = ref(false)

const assignmentsFile = ref(null)
const assignmentsResult = ref(null)
const assignmentsImporting = ref(false)

async function importFile(url, fileRef, resultRef, importingRef) {
  if (!fileRef.value) return
  importingRef.value = true
  resultRef.value = null
  try {
    const formData = new FormData()
    formData.append('file', fileRef.value)
    const response = await fetch(url, { method: 'POST', body: formData })
    resultRef.value = response.ok ? await response.json() : { error: `Import failed: ${response.status}` }
  } finally {
    importingRef.value = false
  }
}

function rowErrors(result) {
  return result.errors.map(e => `Row ${e.rowNumber}: ${e.error}`).join('; ')
}

async function importApplications() {
  await importFile('/api/admin/application-mapping/import/applications', applicationsFile, applicationsResult, applicationsImporting)
}

async function importAssignments() {
  await importFile('/api/admin/application-mapping/import/safe-assignments', assignmentsFile, assignmentsResult, assignmentsImporting)
}
</script>

<template>
  <div>
    <h1>Application Mapping: Bulk Actions</h1>

    <h3>Bulk Import: Applications</h3>
    <p>
      <a href="/api/admin/application-mapping/import/applications/template">Download template</a> --
      one row per application, matched by ApplicationCode. A new code creates the application; an existing code updates it,
      and a blank cell leaves that field as it is.
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" aria-label="Applications CSV file" @change="applicationsFile = $event.target.files[0] ?? null" />
      <button :disabled="!applicationsFile || applicationsImporting" @click="importApplications">{{ applicationsImporting ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="applicationsResult" data-testid="applications-result">
      <template v-if="applicationsResult.error">{{ applicationsResult.error }}</template>
      <template v-else>
        {{ applicationsResult.totalRows }} rows -- {{ applicationsResult.createdCount }} created, {{ applicationsResult.updatedCount }} updated,
        {{ applicationsResult.unchangedCount }} unchanged, {{ applicationsResult.errors.length }} errors
        <span v-if="applicationsResult.errors.length"><br />{{ rowErrors(applicationsResult) }}</span>
      </template>
    </p>

    <h3>Bulk Import: Safe → Application Assignments</h3>
    <p>
      <a href="/api/admin/application-mapping/import/safe-assignments/template">Download template</a> --
      one row per safe: SafeName, Application (its code or name; it must already exist), and an optional Source
      (PrivilegeCloud or SelfHosted). Without a Source, every safe with that name is assigned. An existing assignment is
      replaced; safes not in the file are left alone.
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" aria-label="Safe assignments CSV file" @change="assignmentsFile = $event.target.files[0] ?? null" />
      <button :disabled="!assignmentsFile || assignmentsImporting" @click="importAssignments">{{ assignmentsImporting ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="assignmentsResult" data-testid="assignments-result">
      <template v-if="assignmentsResult.error">{{ assignmentsResult.error }}</template>
      <template v-else>
        {{ assignmentsResult.totalRows }} rows -- {{ assignmentsResult.assignedCount }} safes assigned, {{ assignmentsResult.changedCount }} changed,
        {{ assignmentsResult.unchangedCount }} unchanged, {{ assignmentsResult.errors.length }} errors
        <span v-if="assignmentsResult.errors.length"><br />{{ rowErrors(assignmentsResult) }}</span>
      </template>
    </p>
  </div>
</template>
