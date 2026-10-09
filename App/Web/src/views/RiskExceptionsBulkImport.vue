<script setup>
// D-183: import risk exceptions approved in another tool, reached via the
// Risk Exceptions list's "Bulk Actions" button -- the same upload / Import
// / result-summary pattern as ApplicationSafeMappingBulkImport.vue. Each
// row gets a BlueTrack ExceptionID and keeps its source tool, ID and link.
import { ref } from 'vue'

const file = ref(null)
const result = ref(null)
const importing = ref(false)

async function importFile() {
  if (!file.value) return
  importing.value = true
  result.value = null
  try {
    const formData = new FormData()
    formData.append('file', file.value)
    const response = await fetch('/api/risk-exceptions/import', { method: 'POST', body: formData })
    result.value = response.ok ? await response.json() : { error: `Import failed: ${response.status}` }
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <div>
    <h1>Risk Exceptions: Bulk Actions</h1>

    <h3>Bulk Import: Exceptions from another tool</h3>
    <p><a href="/api/risk-exceptions/import/template">Download template</a> -- one row per exception. Each gets a new BlueTrack Exception ID.</p>
    <ul>
      <li><strong>SourceTool</strong>, <strong>SourceExceptionId</strong> (required): where it came from. The same tool and ID already imported is an error.</li>
      <li><strong>SourceUrl</strong>: a link to it in that tool (http:// or https://).</li>
      <li>What it covers, exactly one of: <strong>AccountUserName</strong> + <strong>AccountAddress</strong> (both required, an exact match; an account without an address can't be matched), or <strong>ApplicationCode</strong>.</li>
      <li><strong>Justification</strong>, <strong>ApprovedByName</strong> (the approver in that tool), <strong>ApprovalDate</strong>, <strong>ReviewDate</strong> (required; dates as yyyy-MM-dd).</li>
      <li><strong>Status</strong>: Active, Expired or Revoked (blank means Active). <strong>ExternalTicketReference</strong> is optional.</li>
      <li><strong>LinkToAccountProgress</strong>: Yes also sets an Active account exception's account to Risk Accepted / Excluded, linked to it (blank means No).</li>
    </ul>
    <p class="filter-row">
      <input type="file" accept=".csv" aria-label="Risk exceptions CSV file" @change="file = $event.target.files[0] ?? null" />
      <button :disabled="!file || importing" @click="importFile">{{ importing ? 'Importing...' : 'Import' }}</button>
    </p>
    <div v-if="result" data-testid="exceptions-result" role="status">
      <p v-if="result.error">{{ result.error }}</p>
      <template v-else>
        <p>{{ result.totalRows }} rows -- {{ result.createdCount }} imported, {{ result.linkedCount }} linked to Account Progress, {{ result.errors.length }} errors</p>
        <ul v-if="result.errors.length">
          <li v-for="e in result.errors" :key="`${e.rowNumber}-${e.error}`">Row {{ e.rowNumber }}: {{ e.error }}</li>
        </ul>
        <p v-if="result.created.length"><small>New IDs: {{ result.created.map(c => `${c.sourceExceptionId} → ${c.exceptionId}`).join(', ') }}</small></p>
      </template>
    </div>
  </div>
</template>
