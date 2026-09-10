<script setup>
// D-124 Phase 5: relocated wholesale off Targets.vue's own always-visible
// inline sections onto this dedicated routed page (targets-bulk-import),
// reached via the "Bulk Actions" link in Targets.vue's header -- consistent
// with how Add/Edit moved to TargetEdit.vue in Phase 4. The file-input +
// Import button + result-summary markup and the backing
// importFile/onInventoryFileChange/onAccountTargetFileChange script logic
// are unchanged from Targets.vue; the one deliberate change is dropping the
// post-import `await load()` call that used to refresh Targets.vue's own
// table -- there's no table on this page, and navigating back to Targets
// re-fetches fresh data on mount anyway.
//
// Targets.vue's separate "Link a Single Account to a Target" section
// (a single-record form, not a CSV/file-upload import) is NOT a bulk-import
// section and stays on Targets.vue untouched.
import { ref } from 'vue'

const inventoryFile = ref(null)
const inventoryImportResult = ref(null)
const inventoryImporting = ref(false)

const accountTargetFile = ref(null)
const accountTargetImportResult = ref(null)
const accountTargetImporting = ref(false)

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

function onInventoryFileChange(event) {
  inventoryFile.value = event.target.files[0] ?? null
}
function onAccountTargetFileChange(event) {
  accountTargetFile.value = event.target.files[0] ?? null
}

async function importInventory() {
  await importFile('/api/admin/risk-scoring/import/target-inventory', inventoryFile, inventoryImportResult, inventoryImporting)
}
async function importAccountTargetMap() {
  await importFile('/api/admin/risk-scoring/import/account-target-map', accountTargetFile, accountTargetImportResult, accountTargetImporting)
}
</script>

<template>
  <div>
    <h1>Targets: Bulk Actions</h1>

    <h3>Bulk Import: Target Inventory</h3>
    <p>
      <a href="/api/admin/risk-scoring/import/target-inventory/template">Download template</a> --
      each row is matched against existing Targets by identifier (auto-merges on a strong match, e.g. ADGuid; a weak IP-only match is queued for review instead of auto-merging).
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" @change="onInventoryFileChange" />
      <button :disabled="!inventoryFile || inventoryImporting" @click="importInventory">{{ inventoryImporting ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="inventoryImportResult">
      {{ inventoryImportResult.totalRows }} rows -- {{ inventoryImportResult.createdCount }} created, {{ inventoryImportResult.mergedCount }} merged, {{ inventoryImportResult.pendingReviewCount }} pending review, {{ inventoryImportResult.errors?.length ?? 0 }} errors.
      <span v-if="inventoryImportResult.errors?.length"><br />{{ inventoryImportResult.errors.map(e => `Row ${e.rowNumber}: ${e.error}`).join('; ') }}</span>
    </p>

    <h3>Bulk Import: Direct Account -&gt; Target Links</h3>
    <p><a href="/api/admin/risk-scoring/import/account-target-map/template">Download template</a> -- rows this app cannot yet match to an existing Target/Account are reported as row errors, not silently skipped.</p>
    <p class="filter-row">
      <input type="file" accept=".csv" @change="onAccountTargetFileChange" />
      <button :disabled="!accountTargetFile || accountTargetImporting" @click="importAccountTargetMap">{{ accountTargetImporting ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="accountTargetImportResult">
      {{ accountTargetImportResult.totalRows }} rows -- {{ accountTargetImportResult.createdCount }} created, {{ accountTargetImportResult.mergedCount }} already linked, {{ accountTargetImportResult.errors?.length ?? 0 }} errors.
      <span v-if="accountTargetImportResult.errors?.length"><br />{{ accountTargetImportResult.errors.map(e => `Row ${e.rowNumber}: ${e.error}`).join('; ') }}</span>
    </p>
  </div>
</template>
