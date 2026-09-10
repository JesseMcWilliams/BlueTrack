<script setup>
// D-124 Phase 5: relocated wholesale off AccessGroups.vue's own
// always-visible inline sections onto this dedicated routed page
// (access-groups-bulk-import), reached via the "Bulk Actions" link in
// AccessGroups.vue's header -- consistent with how Add/Edit moved to
// AccessGroupEdit.vue in Phase 4. The importState/onFileChange/importFile
// script logic and the file-input + Import button + result-summary markup
// for all 3 sections are unchanged from AccessGroups.vue; the one
// deliberate change is dropping the post-import `await load()` call that
// used to refresh AccessGroups.vue's own table -- there's no table on this
// page, and navigating back to Access Groups re-fetches fresh data on
// mount anyway.
import { ref } from 'vue'

const importState = ref({
  inventory: { file: null, result: null, importing: false },
  targetMap: { file: null, result: null, importing: false },
  membership: { file: null, result: null, importing: false }
})

function onFileChange(key, event) {
  importState.value[key].file = event.target.files[0] ?? null
}

async function importFile(key, url) {
  const state = importState.value[key]
  if (!state.file) return
  state.importing = true
  state.result = null
  try {
    const formData = new FormData()
    formData.append('file', state.file)
    const response = await fetch(url, { method: 'POST', body: formData })
    state.result = response.ok ? await response.json() : { error: `Import failed: ${response.status}` }
  } finally {
    state.importing = false
  }
}
</script>

<template>
  <div>
    <h1>Access Groups: Bulk Actions</h1>

    <h3>Bulk Import: Access Group Inventory</h3>
    <p><a href="/api/admin/risk-scoring/import/access-group-inventory/template">Download template</a> -- upserts by GroupIdentifier (a matching row updates the existing group instead of creating a duplicate).</p>
    <p class="filter-row">
      <input type="file" accept=".csv" @change="onFileChange('inventory', $event)" />
      <button :disabled="!importState.inventory.file || importState.inventory.importing" @click="importFile('inventory', '/api/admin/risk-scoring/import/access-group-inventory')">{{ importState.inventory.importing ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="importState.inventory.result">
      {{ importState.inventory.result.totalRows }} rows -- {{ importState.inventory.result.succeededCount }} succeeded, {{ importState.inventory.result.errors?.length ?? 0 }} errors.
      <span v-if="importState.inventory.result.errors?.length"><br />{{ importState.inventory.result.errors.map(e => `Row ${e.rowNumber}: ${e.error}`).join('; ') }}</span>
    </p>

    <h3>Bulk Import: Access Group -&gt; Target Map</h3>
    <p><a href="/api/admin/risk-scoring/import/access-group-target-map/template">Download template</a> -- which Targets each group grants access to (both the group and the target must already exist).</p>
    <p class="filter-row">
      <input type="file" accept=".csv" @change="onFileChange('targetMap', $event)" />
      <button :disabled="!importState.targetMap.file || importState.targetMap.importing" @click="importFile('targetMap', '/api/admin/risk-scoring/import/access-group-target-map')">{{ importState.targetMap.importing ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="importState.targetMap.result">
      {{ importState.targetMap.result.totalRows }} rows -- {{ importState.targetMap.result.succeededCount }} succeeded, {{ importState.targetMap.result.errors?.length ?? 0 }} errors.
      <span v-if="importState.targetMap.result.errors?.length"><br />{{ importState.targetMap.result.errors.map(e => `Row ${e.rowNumber}: ${e.error}`).join('; ') }}</span>
    </p>

    <h3>Bulk Import: Account -&gt; Access Group Membership</h3>
    <p><a href="/api/admin/risk-scoring/import/account-access-group-membership/template">Download template</a> -- which Accounts belong to each group.</p>
    <p class="filter-row">
      <input type="file" accept=".csv" @change="onFileChange('membership', $event)" />
      <button :disabled="!importState.membership.file || importState.membership.importing" @click="importFile('membership', '/api/admin/risk-scoring/import/account-access-group-membership')">{{ importState.membership.importing ? 'Importing...' : 'Import' }}</button>
    </p>
    <p v-if="importState.membership.result">
      {{ importState.membership.result.totalRows }} rows -- {{ importState.membership.result.succeededCount }} succeeded, {{ importState.membership.result.errors?.length ?? 0 }} errors.
      <span v-if="importState.membership.result.errors?.length"><br />{{ importState.membership.result.errors.map(e => `Row ${e.rowNumber}: ${e.error}`).join('; ') }}</span>
    </p>
  </div>
</template>
