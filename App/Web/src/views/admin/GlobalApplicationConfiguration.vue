<script setup>
// Single-row settings form against /api/admin/configuration
// (GlobalApplicationConfigController) -- merges web.app_config and
// web.audit_config. LogReadEvents/RetentionDays are stored and editable
// here, but nothing in the API actually enforces read-logging yet
// (AuditLogger's own comment on why) -- this page manages the setting,
// not the enforcement.
import { ref, onMounted } from 'vue'

const config = ref(null)
const error = ref(null)
const loading = ref(true)
const saved = ref(false)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/configuration')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    config.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function save() {
  saved.value = false
  const response = await fetch('/api/admin/configuration', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(config.value)
  })
  if (!response.ok) {
    error.value = `Save failed: ${response.status}`
    return
  }
  saved.value = true
}
</script>

<template>
  <div>
    <h2>Global Application Configuration</h2>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">{{ error }}</p>
    <form v-else @submit.prevent="save">
      <p>
        <label>Idle Timeout (minutes): <input v-model.number="config.idleTimeoutMinutes" type="number" required /></label>
      </p>
      <p>
        <label>
          Breadcrumb Position:
          <select v-model="config.breadcrumbPosition">
            <option value="TopLeft">Top Left</option>
            <option value="TopRight">Top Right</option>
          </select>
        </label>
      </p>
      <p>
        <label>Exception ID Pattern: <input v-model="config.exceptionIdPattern" required /></label>
        <br /><small>Tokens: {yyyy}, {yy}, {seq:0000} (padding width from the number of zeros)</small>
      </p>
      <p>
        <label>Account Progress Lock Timeout (minutes): <input v-model.number="config.lockTimeoutMinutes" type="number" required /></label>
      </p>
      <p>
        <label>Audit Retention (days, blank = keep forever): <input v-model.number="config.retentionDays" type="number" /></label>
      </p>
      <p>
        <label><input v-model="config.logReadEvents" type="checkbox" /> Log read/view events (off by default, D-35)</label>
      </p>
      <button type="submit">Save</button>
      <span v-if="saved"> Saved.</span>
    </form>
  </div>
</template>
