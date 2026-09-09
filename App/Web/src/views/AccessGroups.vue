<script setup>
// Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for
// Access Groups -- a privileged-access group in the MANAGED environment
// (e.g. an AD "Server Admins" group), distinct from CyberArk's own
// dim_group (Safe permissions) and web.identity_group_role_map (this
// app's own login/authorization mapping). Phase B adds bulk CSV upload for
// the group inventory itself plus its two mapping feeds (which Targets it
// reaches, which Accounts belong to it).
//
// D-121: promoted out of the Admin hub to a top-level nav entry (Analyst
// now has ManageAccessGroups too, same as Admin); adds SOR Type (a new
// controlled dimension, web.dim_sor_type) and SOR Address (free text) --
// additive alongside the existing GroupScope/FoundOnTargetKey, which are
// unchanged -- plus surfaces the already-existing DiscoverySource column,
// previously never shown here. Also adds this page's first-ever filter/sort
// support (scope/SOR type, sortable column headers) and the app-wide
// "Showing N of M total" count summary (X-Total-Count).
import { ref, computed, onMounted, watch } from 'vue'
import { useTotalCount } from '../composables/useTotalCount'
import FilterCountSummary from '../components/FilterCountSummary.vue'

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
  await load()
}

const items = ref([])
const targets = ref([])
const sorTypes = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

const { totalCount, readTotalCount } = useTotalCount()

const scopeFilter = ref('')
const sorTypeFilter = ref('')

const sortColumns = ref([])
const columns = [
  { field: 'groupName', label: 'Name' },
  { field: 'groupIdentifier', label: 'Identifier' },
  { field: 'groupScope', label: 'Scope' },
  { field: 'sorTypeName', label: 'SOR Type' },
  { field: 'baseRiskScore', label: 'Base Risk' },
  { field: 'computedRiskScore', label: 'Computed Risk' }
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
    if (scopeFilter.value) params.set('groupScope', scopeFilter.value)
    if (sorTypeFilter.value) params.set('sorTypeName', sorTypeFilter.value)
    if (sortQueryParam.value) params.set('sort', sortQueryParam.value)

    const [groupsResponse, targetsResponse] = await Promise.all([
      fetch(`/api/admin/access-groups?${params.toString()}`),
      fetch('/api/admin/targets')
    ])
    if (!groupsResponse.ok) throw new Error(`Request failed: ${groupsResponse.status}`)
    if (!targetsResponse.ok) throw new Error(`Request failed: ${targetsResponse.status}`)
    readTotalCount(groupsResponse)
    items.value = await groupsResponse.json()
    targets.value = await targetsResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const sorTypesResponse = await fetch('/api/admin/access-groups/sor-types')
    if (sorTypesResponse.ok) sorTypes.value = await sorTypesResponse.json()
  } catch {
    // Non-fatal -- the SOR Type dropdown just won't have options if this fails.
  }
  await load()
})

watch([scopeFilter, sorTypeFilter, sortQueryParam], load)

function startCreate() {
  editing.value = { groupName: '', groupIdentifier: '', groupScope: 'Domain', foundOnTargetKey: null, sorTypeKey: null, sorAddress: '', baseRiskScore: 0, description: '', discoverySource: '' }
}
function startEdit(item) {
  editing.value = { ...item }
}
function cancelEdit() {
  editing.value = null
}

async function save() {
  const isNew = editing.value.accessGroupKey === undefined
  const url = isNew ? '/api/admin/access-groups' : `/api/admin/access-groups/${editing.value.accessGroupKey}`
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
  const response = await fetch(`/api/admin/access-groups/${item.accessGroupKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status} (a group still referenced by a Target/Account mapping can't be deleted)`
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h1>Access Groups</h1>
    <p>Privileged-access groups in the managed environment (e.g. an AD "Server Admins" group) -- not this app's own Safe-permission groups or its login/role mapping. Base risk score (0-1000) is analyst-set; the computed score (base plus reachable Targets) is calculated separately.</p>
    <p v-if="error" role="alert">{{ error }}</p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">Scope:</span>
        <select v-model="scopeFilter">
          <option value="">All</option>
          <option value="Domain">Domain</option>
          <option value="Local">Local</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">SOR Type:</span>
        <select v-model="sorTypeFilter">
          <option value="">All</option>
          <option v-for="type in sorTypes" :key="type.sorTypeKey" :value="type.sorTypeName">{{ type.sorTypeName }}</option>
        </select>
      </label>
    </p>
    <FilterCountSummary :shown="items.length" :total="totalCount" />

    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <button class="btn-primary" @click="startCreate">+ New Access Group</button>

      <table>
        <thead>
          <tr>
            <th v-for="col in columns" :key="col.field" :aria-sort="ariaSortFor(col.field)">
              <button type="button" @click="toggleSort(col.field, $event)">
                {{ col.label }} <span aria-hidden="true">{{ sortIndicator(col.field) }}</span>
              </button>
            </th>
            <th>SOR Address</th>
            <th>Discovery Source</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.accessGroupKey">
            <td>{{ item.groupName }}</td>
            <td>{{ item.groupIdentifier }}</td>
            <td>{{ item.groupScope }}<span v-if="item.foundOnTargetName"> ({{ item.foundOnTargetName }})</span></td>
            <td>{{ item.sorTypeName ?? '—' }}</td>
            <td>{{ item.baseRiskScore }}</td>
            <td>{{ item.computedRiskScore ?? '(not yet calculated)' }}<span v-if="item.isRiskScoreStale"> (stale)</span></td>
            <td>{{ item.sorAddress ?? '—' }}</td>
            <td>{{ item.discoverySource ?? '—' }}</td>
            <td>
              <button @click="startEdit(item)">Edit</button>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p><small>Click a column to sort by it; shift-click another column to add it as a secondary sort key.</small></p>

      <form v-if="editing" @submit.prevent="save">
        <h3>{{ editing.accessGroupKey === undefined ? 'New Access Group' : 'Edit Access Group' }}</h3>
        <p><label class="field-label"><span class="field-label-text">Name:</span> <input v-model="editing.groupName" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Identifier (e.g. AD SID/DN):</span> <input v-model="editing.groupIdentifier" required /></label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">Scope:</span>
            <select v-model="editing.groupScope">
              <option value="Domain">Domain</option>
              <option value="Local">Local</option>
            </select>
          </label>
        </p>
        <p v-if="editing.groupScope === 'Local'">
          <label class="field-label">
            <span class="field-label-text">Found On Target:</span>
            <select v-model="editing.foundOnTargetKey">
              <option :value="null">(none)</option>
              <option v-for="target in targets" :key="target.targetKey" :value="target.targetKey">{{ target.targetName }}</option>
            </select>
          </label>
        </p>
        <p>
          <label class="field-label">
            <span class="field-label-text">SOR Type:</span>
            <select v-model="editing.sorTypeKey">
              <option :value="null">(none)</option>
              <option v-for="type in sorTypes" :key="type.sorTypeKey" :value="type.sorTypeKey">{{ type.sorTypeName }}</option>
            </select>
          </label>
        </p>
        <p><label class="field-label"><span class="field-label-text">SOR Address:</span> <input v-model="editing.sorAddress" placeholder="e.g. company.com or 192.168.1.1" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Base Risk Score (0-1000):</span> <input v-model.number="editing.baseRiskScore" type="number" min="0" max="1000" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Discovery Source:</span> <input v-model="editing.discoverySource" placeholder="Manual" /></label></p>
        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>

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
    </template>
  </div>
</template>
