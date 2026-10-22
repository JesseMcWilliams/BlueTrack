<script setup>
// D-182: apply the same values to every account selected on the Account
// Progress list. Only fields whose "Change" box is ticked are sent; every
// other field of each account stays as it is. The API saves each account
// with the edit page's own rules, skips accounts someone else is editing,
// and reports what it skipped and why. Risk Exception isn't offered: a link
// must be an exception scoped to one account.
// D-291: a field with a RequiredPermission the user lacks can't be ticked.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import { useAccountSelectionStore } from '../stores/accountSelection'
import { useListStateStore } from '../stores/listState'
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../composables/useConfirmDialog'

const { t } = useI18n()

const router = useRouter()
const listState = useListStateStore()
const selection = useAccountSelectionStore()
const rights = useRightsStore()

const referenceData = ref({})
const saving = ref(false)
const error = ref(null)
const referenceError = ref(null) // UI review W2-9: without the reference data the dropdowns are empty, so say so
const result = ref(null)
const requiredPermissions = ref({}) // field name -> permission name, from the field metadata

const fields = [
  { field: 'CurrentStageKey', labelKey: 'pages.accountProgressBulkEdit.fieldStage', type: 'select', table: 'dim_blueprint_stage', required: true },
  { field: 'CurrentStatusKey', labelKey: 'pages.accountProgressBulkEdit.fieldStatus', type: 'select', table: 'dim_progress_status', required: true },
  { field: 'RiskLevelKey', labelKey: 'pages.accountProgressBulkEdit.fieldRiskLevel', type: 'select', table: 'dim_risk_level' },
  { field: 'AccountTypeKey', labelKey: 'pages.accountProgressBulkEdit.fieldAccountType', type: 'select', table: 'dim_account_type' },
  { field: 'SORKey', labelKey: 'pages.accountProgressBulkEdit.fieldSourceOfRecord', type: 'select', table: 'dim_source_of_record' },
  { field: 'ApplicationKey', labelKey: 'pages.accountProgressBulkEdit.fieldApplication', type: 'select', table: 'dim_application' },   // D-239
  { field: 'OwnerName', labelKey: 'pages.accountProgressBulkEdit.fieldOwner', type: 'text' },
  { field: 'BusinessUnit', labelKey: 'pages.accountProgressBulkEdit.fieldBusinessUnit', type: 'text' },
  { field: 'TargetRemediationDate', labelKey: 'pages.accountProgressBulkEdit.fieldTargetRemediationDate', type: 'date' },
  { field: 'ActualCompletionDate', labelKey: 'pages.accountProgressBulkEdit.fieldActualCompletionDate', type: 'date' },
  { field: 'Notes', labelKey: 'pages.accountProgressBulkEdit.fieldNotes', type: 'notes' }
]

const change = ref(Object.fromEntries(fields.map(f => [f.field, false])))
const values = ref(Object.fromEntries(fields.map(f => [f.field, null])))
const notesMode = ref('Append')
const reason = ref('')

const chosen = computed(() => fields.filter(f => change.value[f.field]).map(f => f.field))

function lockedBy(f) {
  const permission = requiredPermissions.value[f.field]
  return permission && !rights.hasPermission(permission) ? permission : null
}

onMounted(async () => {
  try {
    const response = await fetch('/api/account-progress/reference-data')
    if (response.ok) referenceData.value = await response.json()
    else referenceError.value = t('pages.accountProgressBulkEdit.referenceFailed', { status: response.status })
  } catch {
    referenceError.value = t('pages.accountProgressBulkEdit.referenceFailed', { status: 0 })
  }
  try {
    // Without it every field stays offered; the API still skips a change the user may not make.
    const response = await fetch('/api/account-progress/field-metadata')
    if (response.ok) {
      requiredPermissions.value = Object.fromEntries((await response.json())
        .filter(m => m.requiredPermissionName)
        .map(m => [m.fieldName, m.requiredPermissionName]))
    }
  } catch {
    // as above
  }
})

async function apply() {
  // UI review W2-6: this overwrites fields on every selected account, so it asks first.
  const confirmed = await confirmDelete({
    title: t('pages.accountProgressBulkEdit.confirmTitle', { count: formatCount(selection.count) }),
    // a ticked field left empty clears it on every selected account, so the confirmation says so
    details: fields.filter(f => change.value[f.field]).map(f => (values.value[f.field] === '' || values.value[f.field] === null ? t('pages.accountProgressBulkEdit.confirmCleared', { field: t(f.labelKey) }) : t(f.labelKey))),
    warning: t('pages.accountProgressBulkEdit.confirmWarning')
  }, t('pages.accountProgressBulkEdit.confirmButton'))
  if (!confirmed) return
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
      error.value = problem?.detail ?? t('pages.accountProgressBulkEdit.failed', { status: response.status })
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
  // UI review W2-2: the list as it was left (its filters, sort and page).
  router.push({ name: 'account-progress-list', query: listState.queryFor('account-progress-list') })
}
</script>

<template>
  <div>
    <h1>{{ t('pages.accountProgressBulkEdit.title') }}</h1>
    <i18n-t v-if="!result && selection.count === 0" keypath="pages.accountProgressBulkEdit.noneSelected" tag="p" scope="global">
      <template #link><router-link :to="{ name: 'account-progress-list', query: listState.queryFor('account-progress-list') }">{{ t('pages.accountProgressBulkEdit.accountsLink') }}</router-link></template>
    </i18n-t>

    <form v-else-if="!result" @submit.prevent="apply">
      <p v-if="referenceError" role="alert">{{ referenceError }}</p>
      <i18n-t keypath="pages.accountProgressBulkEdit.intro" tag="p" scope="global">
        <template #count><strong>{{ formatCount(selection.count) }}</strong></template>
        <template #change><em>{{ t('pages.accountProgressBulkEdit.change') }}</em></template>
      </i18n-t>
      <table>
        <thead><tr><th>{{ t('pages.accountProgressBulkEdit.change') }}</th><th>{{ t('pages.accountProgressBulkEdit.field') }}</th><th>{{ t('pages.accountProgressBulkEdit.newValue') }}</th></tr></thead>
        <tbody>
          <tr v-for="f in fields" :key="f.field">
            <td><input v-model="change[f.field]" type="checkbox" :disabled="!!lockedBy(f)" :aria-label="t('pages.accountProgressBulkEdit.changeField', { field: t(f.labelKey) })" /></td>
            <td>{{ t(f.labelKey) }}<span v-if="lockedBy(f)" class="hint"> {{ t('pages.accountProgressBulkEdit.fieldNeedsPermission', { permission: lockedBy(f) }) }}</span></td>
            <td>
              <select v-if="f.type === 'select'" v-model="values[f.field]" :disabled="!change[f.field]" :aria-label="t(f.labelKey)" :required="change[f.field] && f.required">
                <option v-if="!f.required" :value="null">{{ t('common.blank') }}</option>
                <option v-for="opt in referenceData[f.table] ?? []" :key="opt.key" :value="opt.key">{{ opt.name }}</option>
              </select>
              <input v-else-if="f.type === 'text'" v-model="values[f.field]" :disabled="!change[f.field]" :aria-label="t(f.labelKey)" :placeholder="t('pages.accountProgressBulkEdit.blankClears')" />
              <input v-else-if="f.type === 'date'" v-model="values[f.field]" type="date" :disabled="!change[f.field]" :aria-label="t(f.labelKey)" />
              <template v-else>
                <select v-model="notesMode" :disabled="!change[f.field]" :aria-label="t('pages.accountProgressBulkEdit.notesMode')">
                  <option value="Append">{{ t('pages.accountProgressBulkEdit.notesAppend') }}</option>
                  <option value="Replace">{{ t('pages.accountProgressBulkEdit.notesReplace') }}</option>
                </select>
                <br />
                <textarea v-model="values[f.field]" :disabled="!change[f.field]" rows="3" cols="50" :aria-label="t('pages.accountProgressBulkEdit.fieldNotes')"></textarea>
              </template>
            </td>
          </tr>
        </tbody>
      </table>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressBulkEdit.reason') }}</span> <input v-model="reason" size="60" /></label>
        <br /><small>{{ t('pages.accountProgressBulkEdit.reasonHint') }}</small>
      </p>
      <p><small>{{ t('pages.accountProgressBulkEdit.riskExceptionHint') }}</small></p>
      <p v-if="error" role="alert">{{ error }}</p>
      <button type="submit" class="btn-primary" :disabled="saving || chosen.length === 0">{{ t('pages.accountProgressBulkEdit.apply', { count: formatCount(selection.count) }) }}</button>
      <button type="button" @click="backToList">{{ t('common.cancel') }}</button>
      <span v-if="saving" role="status">{{ ' ' + t('pages.accountProgressBulkEdit.saving') }}</span>
    </form>

    <div v-else role="status">
      <p>{{ t('pages.accountProgressBulkEdit.result', { updated: formatCount(result.updated), unchanged: formatCount(result.unchanged), skipped: formatCount(result.skipped.length) }) }}</p>
      <template v-if="result.skipped.length">
        <p>{{ t('pages.accountProgressBulkEdit.skippedTitle') }}</p>
        <table>
          <thead><tr><th>{{ t('pages.accountProgressBulkEdit.account') }}</th><th>{{ t('pages.accountProgressBulkEdit.why') }}</th></tr></thead>
          <tbody>
            <tr v-for="s in result.skipped" :key="s.accountKey">
              <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: s.accountKey } }">{{ s.accountName ?? s.accountKey }}</router-link></td>
              <td>{{ s.reason }}</td>
            </tr>
          </tbody>
        </table>
      </template>
      <button type="button" class="btn-primary" @click="backToList">{{ t('pages.accountProgressBulkEdit.backToAccounts') }}</button>
    </div>
  </div>
</template>
