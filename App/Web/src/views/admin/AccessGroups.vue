<script setup>
// Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for
// Access Groups -- a privileged-access group in the MANAGED environment
// (e.g. an AD "Server Admins" group), distinct from CyberArk's own
// dim_group (Safe permissions) and web.identity_group_role_map (this
// app's own login/authorization mapping). Bulk CSV upload comes in Phase B.
import { ref, onMounted } from 'vue'

const items = ref([])
const targets = ref([])
const error = ref(null)
const loading = ref(true)
const editing = ref(null)

async function load() {
  loading.value = true
  try {
    const [groupsResponse, targetsResponse] = await Promise.all([
      fetch('/api/admin/access-groups'),
      fetch('/api/admin/targets')
    ])
    if (!groupsResponse.ok) throw new Error(`Request failed: ${groupsResponse.status}`)
    if (!targetsResponse.ok) throw new Error(`Request failed: ${targetsResponse.status}`)
    items.value = await groupsResponse.json()
    targets.value = await targetsResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

function startCreate() {
  editing.value = { groupName: '', groupIdentifier: '', groupScope: 'Domain', foundOnTargetKey: null, baseRiskScore: 0, description: '', discoverySource: '' }
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
    <h2>Access Groups</h2>
    <p>Privileged-access groups in the managed environment (e.g. an AD "Server Admins" group) -- not this app's own Safe-permission groups or its login/role mapping. Base risk score (0-1000) is analyst-set; the computed score (base plus reachable Targets) is calculated separately.</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <button class="btn-primary" @click="startCreate">+ New Access Group</button>

      <table>
        <thead>
          <tr><th>Name</th><th>Identifier</th><th>Scope</th><th>Base Risk</th><th>Computed Risk</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.accessGroupKey">
            <td>{{ item.groupName }}</td>
            <td>{{ item.groupIdentifier }}</td>
            <td>{{ item.groupScope }}<span v-if="item.foundOnTargetName"> ({{ item.foundOnTargetName }})</span></td>
            <td>{{ item.baseRiskScore }}</td>
            <td>{{ item.computedRiskScore ?? '(not yet calculated)' }}<span v-if="item.isRiskScoreStale"> (stale)</span></td>
            <td>
              <button @click="startEdit(item)">Edit</button>
              <button @click="remove(item)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>

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
        <p><label class="field-label"><span class="field-label-text">Base Risk Score (0-1000):</span> <input v-model.number="editing.baseRiskScore" type="number" min="0" max="1000" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Description:</span> <input v-model="editing.description" /></label></p>
        <p><label class="field-label"><span class="field-label-text">Discovery Source:</span> <input v-model="editing.discoverySource" placeholder="Manual" /></label></p>
        <button type="submit" class="btn-primary">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>
    </template>
  </div>
</template>
