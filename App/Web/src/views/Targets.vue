<script setup>
// Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for
// the Target inventory. Each Target's identifiers (web.target_identifier)
// are a small nested collection edited alongside the Target itself --
// replace-all-on-save (TargetRepository's own comment on why).
// Phase B adds bulk CSV upload (Target inventory + direct Account->Target
// links) against RiskScoringImportController.
//
// D-121: promoted out of the Admin hub to a top-level nav entry (Analyst
// now has ManageTargets too, same as Admin) -- and, since this page
// previously had zero filter/sort support at all, this adds it fresh:
// stacked filters (type/application) plus sortable column headers, same
// click/shift-click multi-column sort pattern as the D-42 pages, plus the
// app-wide "Showing N of M total" count summary (X-Total-Count).
import { ref, computed, onMounted, watch } from 'vue'
import { useTotalCount } from '../composables/useTotalCount'
import FilterCountSummary from '../components/FilterCountSummary.vue'

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
const applications = ref([])
const identifierTypes = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

const { totalCount, readTotalCount } = useTotalCount()

const typeFilter = ref('')
const applicationFilter = ref('')

const sortColumns = ref([])
const columns = [
  { field: 'targetName', label: 'Name' },
  { field: 'targetType', label: 'Type' },
  { field: 'applicationName', label: 'Application' },
  { field: 'riskScore', label: 'Risk Score' }
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

async function load() {
  loading.value = true
  try {
    const params = new URLSearchParams()
    if (typeFilter.value) params.set('targetType', typeFilter.value)
    if (applicationFilter.value) params.set('applicationKey', applicationFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)

    const [targetsResponse, typesResponse] = await Promise.all([
      fetch(`/api/admin/targets?${params.toString()}`),
      fetch('/api/admin/targets/identifier-types')
    ])
    if (!targetsResponse.ok) throw new Error(`Request failed: ${targetsResponse.status}`)
    if (!typesResponse.ok) throw new Error(`Request failed: ${typesResponse.status}`)
    readTotalCount(targetsResponse)
    items.value = await targetsResponse.json()
    identifierTypes.value = await typesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const appsResponse = await fetch('/api/applications')
    if (appsResponse.ok) applications.value = await appsResponse.json()
  } catch {
    // Non-fatal -- the application filter just won't have dropdown options if this fails.
  }
  await load()
})

watch([typeFilter, applicationFilter, sortQueryParam], load)

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
    <h1>Targets</h1>
    <p>Any final destination an account's access leads to -- a server, database, application, or other endpoint. Risk score (0-1000) is always analyst-set here, regardless of how the row itself was created.</p>
    <p v-if="error" role="alert">{{ error }}</p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">Type:</span>
        <select v-model="typeFilter">
          <option value="">All</option>
          <option v-for="type in TARGET_TYPES" :key="type" :value="type">{{ type }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Application:</span>
        <select v-model="applicationFilter">
          <option value="">All</option>
          <option v-for="app in applications" :key="app.applicationKey" :value="app.applicationKey">{{ app.applicationName }}</option>
        </select>
      </label>
    </p>
    <FilterCountSummary :shown="items.length" :total="totalCount" />

    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <button class="btn-primary" @click="startCreate">+ New Target</button>

      <table>
        <thead>
          <tr>
            <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
              <button type="button" @click="toggleSort(col.field, $event)">
                {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
              </button>
            </th>
            <th>Identifiers</th>
            <th></th>
          </tr>
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
      <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>

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
