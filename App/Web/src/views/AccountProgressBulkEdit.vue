<script setup>
// D-182: apply the same values to every account selected on the Account
// Progress list. Only fields whose "Change" box is ticked are sent; every
// other field of each account stays as it is. The API saves each account
// with the edit page's own rules, skips accounts someone else is editing,
// and reports what it skipped and why. Risk Exception isn't offered: a link
// must be an exception scoped to one account.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAccountSelectionStore } from '../stores/accountSelection'

const router = useRouter()
const selection = useAccountSelectionStore()

const referenceData = ref({})
const saving = ref(false)
const error = ref(null)
const result = ref(null)

const fields = [
  { field: 'CurrentStageKey', label: 'Stage', type: 'select', table: 'dim_blueprint_stage', required: true },
  { field: 'CurrentStatusKey', label: 'Status', type: 'select', table: 'dim_progress_status', required: true },
  { field: 'RiskLevelKey', label: 'Risk Level', type: 'select', table: 'dim_risk_level' },
  { field: 'AccountTypeKey', label: 'Account Type', type: 'select', table: 'dim_account_type' },
  { field: 'SORKey', label: 'Source of Record', type: 'select', table: 'dim_source_of_record' },
  { field: 'OwnerName', label: 'Owner', type: 'text' },
  { field: 'BusinessUnit', label: 'Business Unit', type: 'text' },
  { field: 'TargetRemediationDate', label: 'Target Remediation Date', type: 'date' },
  { field: 'ActualCompletionDate', label: 'Actual Completion Date', type: 'date' },
  { field: 'Notes', label: 'Notes', type: 'notes' }
]

const change = ref(Object.fromEntries(fields.map(f => [f.field, false])))
const values = ref(Object.fromEntries(fields.map(f => [f.field, null])))
const notesMode = ref('Append')
const reason = ref('')

const chosen = computed(() => fields.filter(f => change.value[f.field]).map(f => f.field))

onMounted(async () => {
  const response = await fetch('/api/account-progress/reference-data')
  if (response.ok) referenceData.value = await response.json()
})

async function apply() {
  error.value = null
  result.value = null
  saving.value = true
  try {
    const body = { accountKeys: selection.keys, fields: chosen.value, reason: reason.value || null, notesMode: notesMode.value }
    for (const f of chosen.value) body[f.charAt(0).toLowerCase() + f.slice(1)] = values.value[f] === '' ? null : values.value[f]
    const response = await fetch('/api/account-progress/bulk-edit', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      error.value = problem?.detail ?? `Bulk edit failed: ${response.status}`
      return
    }
    result.value = await response.json()
    // Keep only the skipped accounts selected, so they can be retried or reviewed.
    selection.clear()
    selection.add(result.value.skipped.map(s => s.accountKey))
  } finally {
    saving.value = false
  }
}

function backToList() {
  router.push({ name: 'account-progress-list' })
}
</script>

<template>
  <div>
    <h1>Bulk Edit Accounts</h1>
    <p v-if="!result && selection.count === 0">No accounts are selected. Select them on the <router-link :to="{ name: 'account-progress-list' }">Accounts</router-link> list first.</p>

    <form v-else-if="!result" @submit.prevent="apply">
      <p><strong>{{ selection.count }}</strong> accounts selected. Tick <em>Change</em> for each field to set; the others stay as they are on each account.</p>
      <table>
        <thead><tr><th>Change</th><th>Field</th><th>New value</th></tr></thead>
        <tbody>
          <tr v-for="f in fields" :key="f.field">
            <td><input v-model="change[f.field]" type="checkbox" :aria-label="`Change ${f.label}`" /></td>
            <td>{{ f.label }}</td>
            <td>
              <select v-if="f.type === 'select'" v-model="values[f.field]" :disabled="!change[f.field]" :aria-label="f.label" :required="change[f.field] && f.required">
                <option v-if="!f.required" :value="null">(blank)</option>
                <option v-for="opt in referenceData[f.table] ?? []" :key="opt.key" :value="opt.key">{{ opt.name }}</option>
              </select>
              <input v-else-if="f.type === 'text'" v-model="values[f.field]" :disabled="!change[f.field]" :aria-label="f.label" placeholder="blank clears it" />
              <input v-else-if="f.type === 'date'" v-model="values[f.field]" type="date" :disabled="!change[f.field]" :aria-label="f.label" />
              <template v-else>
                <select v-model="notesMode" :disabled="!change[f.field]" aria-label="Notes mode">
                  <option value="Append">Add to the existing notes</option>
                  <option value="Replace">Replace the notes</option>
                </select>
                <br />
                <textarea v-model="values[f.field]" :disabled="!change[f.field]" rows="3" cols="50" aria-label="Notes"></textarea>
              </template>
            </td>
          </tr>
        </tbody>
      </table>
      <p>
        <label class="field-label"><span class="field-label-text">Reason:</span> <input v-model="reason" size="60" /></label>
        <br /><small>Required if the new Stage is earlier than an account's current stage; recorded in the audit log.</small>
      </p>
      <p><small>Risk Exception can't be bulk edited: it must be linked on each account. Accounts someone else is editing are skipped.</small></p>
      <p v-if="error" role="alert">{{ error }}</p>
      <button type="submit" class="btn-primary" :disabled="saving || chosen.length === 0">Apply to {{ selection.count }} accounts</button>
      <button type="button" @click="backToList">Cancel</button>
      <span v-if="saving" role="status"> Saving...</span>
    </form>

    <div v-else role="status">
      <p>{{ result.updated }} updated, {{ result.unchanged }} already had these values, {{ result.skipped.length }} skipped.</p>
      <template v-if="result.skipped.length">
        <p>Skipped accounts (still selected):</p>
        <table>
          <thead><tr><th>Account</th><th>Why</th></tr></thead>
          <tbody>
            <tr v-for="s in result.skipped" :key="s.accountKey">
              <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: s.accountKey } }">{{ s.accountName ?? s.accountKey }}</router-link></td>
              <td>{{ s.reason }}</td>
            </tr>
          </tbody>
        </table>
      </template>
      <button type="button" class="btn-primary" @click="backToList">Back to Accounts</button>
    </div>
  </div>
</template>
