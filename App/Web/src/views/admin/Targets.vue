<script setup>
// Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for
// the Target inventory. Each Target's identifiers (web.target_identifier)
// are a small nested collection edited alongside the Target itself --
// replace-all-on-save (TargetRepository's own comment on why).
// Phase B adds bulk CSV upload (Target inventory + direct Account->Target
// links) against RiskScoringImportController.
import { ref, onMounted } from 'vue'

const inventoryFile = ref(null)
const inventoryImportResult = ref(null)
const inventoryImporting = ref(false)

const accountTargetFile = ref(null)
const accountTargetImportResult = ref(null)
const accountTargetImporting = ref(false)

const linkAccountName = ref('')
const linkTargetKey = ref(null)
const linkError = ref(null)
const linkSaved = ref(false)

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
  await load()
}
async function importAccountTargetMap() {
  await importFile('/api/admin/risk-scoring/import/account-target-map', accountTargetFile, accountTargetImportResult, accountTargetImporting)
}

async function createAccountTargetLink() {
  linkError.value = null
  linkSaved.value = false
  const response = await fetch('/api/admin/risk-scoring/account-target-links', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ accountName: linkAccountName.value, targetKey: linkTargetKey.value })
  })
  if (!response.ok) {
    linkError.value = `Link failed: ${response.status}`
    return
  }
  linkSaved.value = true
  linkAccountName.value = ''
  linkTargetKey.value = null
}

const TARGET_TYPES = ['Server', 'Desktop', 'Database', 'Application', 'LdapDirectory', 'Appliance', 'Other']

const items = ref([])
const identifierTypes = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

async function load() {
  loading.value = true
  try {
    const [targetsResponse, typesResponse] = await Promise.all([
      fetch('/api/admin/targets'),
      fetch('/api/admin/targets/identifier-types')
    ])
    if (!targetsResponse.ok) throw new Error(`Request failed: ${targetsResponse.status}`)
    if (!typesResponse.ok) throw new Error(`Request failed: ${typesResponse.status}`)
    items.value = await targetsResponse.json()
    identifierTypes.value = await typesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function startCreate() {
  editing.value = { targetType: 'Server', targetName: '', riskScore: 0, description: '', discoverySource: '', identifiers: [] }
}
function startEdit(item) {
  editing.value = { ...item, identifiers: item.identifiers.map(i => ({ ...i })) }
}
function cancelEdit() {
  editing.value = null
}

function addIdentifierRow() {
  editing.value.identifiers.push({ identifierType: identifierTypes.value[0]?.identifierType ?? '', identifierValue: '' })
}
function removeIdentifierRow(index) {
  editing.value.identifiers.splice(index, 1)
}

async function save() {
  const isNew = editing.value.targetKey === undefined
  const url = isNew ? '/api/admin/targets' : `/api/admin/targets/${editing.value.targetKey}`
  const response = await fetch(url, {
    method: isNew ? 'POST' : 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(editing.value)
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  editing.value = null
  await load()
}

async function remove(item) {
  const response = await fetch(`/api/admin/targets/${item.targetKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status} (a target still referenced by an Access Group or Account mapping can't be deleted)`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>Targets</h2>
    <p>Any final destination an account's access leads to -- a server, database, application, or other endpoint. Risk score (0-1000) is always analyst-set here, regardless of how the row itself was created.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <button class="btn-primary" @click="startCreate">+ New Target</button>

      <table>
        <thead>
          <tr><th>Name</th><th>Type</th><th>Application</th><th>Risk Score</th><th>Identifiers</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.targetKey">
            <td>{{ item.targetName }}</td>
            <td>{{ item.targetType }}</td>
            <td>{{ item.applicationName }}</td>
            <td>{{ item.riskScore }}</td>
            <td>{{ item.identifiers.map(i => `${i.identifierType}=${i.identifierValue}`).join(', ') }}</td>
            <td>
              <button @click="startEdit(item)">Edit</button>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.targetKey === undefined ? 'New Target' : 'Edit Target' }}</h3>
        <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.targetName" required /></label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">Type:</span>
            <select v-model="editing.targetType">
              <option v-for="type in TARGET_TYPES" :key="type" :value="type">{{ type }}</option>
            </select>
          </label>
        </p>
        <p><label class="field-label"><span class="field-label-text">Risk Score (0-1000):</span> <input v-model.number="editing.riskScore" type="number" min="0" max="1000" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Discovery Source:</span> <input v-model="editing.discoverySource" placeholder="Manual" /></label></p>

        <h4>Identifiers</h4>
        <div v-for="(identifier, index) in editing.identifiers" :key="index" class="filter-row">
          <label class="field-label">
            <span class="field-label-text">Type:</span>
            <select v-model="identifier.identifierType">
              <option v-for="type in identifierTypes" :key="type.identifierType" :value="type.identifierType">{{ type.identifierType }}</option>
            </select>
          </label>
          <label class="field-label"><span class="field-label-text">Value:</span> <input v-model="identifier.identifierValue" required /></label>
          <button type="button" @click="removeIdentifierRow(index)">Remove</button>
        </div>
        <p><button type="button" @click="addIdentifierRow">+ Add Identifier</button></p>

        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>

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

      <h4>Link a Single Account to a Target</h4>
      <p v-if="linkError" role="alert">{{ linkError }}</p>
      <p v-if="linkSaved" role="status">Linked.</p>
      <form class="filter-row" @submit.prevent="createAccountTargetLink">
        <label class="field-label"><span class="field-label-text">Account Name:</span> <input v-model="linkAccountName" required /></label>
        <label class="field-label">
          <span class="field-label-text">Target:</span>
          <select v-model="linkTargetKey" required>
            <option :value="null" disabled>(choose)</option>
            <option v-for="item in items" :key="item.targetKey" :value="item.targetKey">{{ item.targetName }}</option>
          </select>
        </label>
        <button type="submit" class="btn-primary">Link</button>
      </form>
    </template>
  </div>
</template>
