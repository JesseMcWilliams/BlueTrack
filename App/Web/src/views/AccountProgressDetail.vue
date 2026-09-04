<script setup>
// Field-metadata-driven edit form (Design_Interface_Extensibility.md) with
// pessimistic locking (D-50) and the two validation rules from D-51
// (enforced server-side; this form just surfaces whatever error comes back).
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'

const props = defineProps({ accountKey: { type: [String, Number], required: true } })

// Maps account_progress_field_metadata.FieldName (PascalCase, matches the
// C# property) to the camelCase key System.Text.Json actually serializes
// it as -- NOT a mechanical first-letter lowercase (that breaks "SORKey",
// which serializes as "sorKey", not "sORKey"). Verified against a real
// GetDetail response rather than assumed.
const formKeyByFieldName = {
  CurrentStageKey: 'currentStageKey',
  CurrentStatusKey: 'currentStatusKey',
  RiskLevelKey: 'riskLevelKey',
  AccountTypeKey: 'accountTypeKey',
  SORKey: 'sorKey',
  OwnerName: 'ownerName',
  BusinessUnit: 'businessUnit',
  TargetRemediationDate: 'targetRemediationDate',
  ActualCompletionDate: 'actualCompletionDate',
  Notes: 'notes'
}

const fieldMetadata = ref([])
const referenceData = ref({})
const detail = ref(null)
const form = ref({})
const reason = ref('')

const lockStatus = ref(null) // null = unlocked; otherwise { lockedByUserKey, lockedByName, lockedAt, ... }
const lockedByMe = ref(false)

const loading = ref(true)
const error = ref(null)
const saveError = ref(null)
const saving = ref(false)

let heartbeatTimer = null

const sortedFields = computed(() => [...fieldMetadata.value].sort((a, b) => a.displayOrder - b.displayOrder))

// Risk Exception wiring (Design_Risk_Exception_Tracking.md workflow steps
// 1-2): status can't be set to Risk Accepted / Excluded without linking an
// Active exception scoped to this account -- the API enforces this, this
// just gives the form a way to pick or create one before saving.
const selectedExceptionKey = ref('')
const linkableExceptions = ref([])
const exceptionError = ref(null)
const showCreateExceptionForm = ref(false)
const newException = ref({ justification: '', reviewDate: '', externalTicketReference: '' })
const creatingException = ref(false)

const riskAcceptedStatusKey = computed(() =>
  (referenceData.value.dim_progress_status ?? []).find(o => o.name === 'Risk Accepted / Excluded')?.key ?? null)
const isRiskAccepted = computed(() =>
  riskAcceptedStatusKey.value !== null && Number(form.value.currentStatusKey) === riskAcceptedStatusKey.value)

watch(isRiskAccepted, async (nowRiskAccepted) => {
  if (nowRiskAccepted) {
    await loadLinkableExceptions()
    selectedExceptionKey.value = detail.value?.exceptionKey ?? ''
  }
})

async function loadLinkableExceptions() {
  exceptionError.value = null
  try {
    const response = await fetch(`/api/risk-exceptions?accountKey=${props.accountKey}&status=Active`)
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    linkableExceptions.value = await response.json()
  } catch (err) {
    exceptionError.value = err.message
  }
}

async function createInlineException() {
  creatingException.value = true
  exceptionError.value = null
  try {
    const response = await fetch('/api/risk-exceptions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        accountKey: Number(props.accountKey),
        justification: newException.value.justification,
        reviewDate: newException.value.reviewDate,
        externalTicketReference: newException.value.externalTicketReference || null
      })
    })
    if (!response.ok) {
      exceptionError.value = `Could not create exception: ${response.status}`
      return
    }
    const created = await response.json()
    await loadLinkableExceptions()
    selectedExceptionKey.value = created.exceptionKey
    showCreateExceptionForm.value = false
    newException.value = { justification: '', reviewDate: '', externalTicketReference: '' }
  } finally {
    creatingException.value = false
  }
}

function optionsFor(field) {
  return referenceData.value[field.referenceTable] ?? []
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const [metaResponse, refResponse, detailResponse, lockResponse] = await Promise.all([
      fetch('/api/account-progress/field-metadata'),
      fetch('/api/account-progress/reference-data'),
      fetch(`/api/account-progress/${props.accountKey}`),
      fetch(`/api/account-progress/${props.accountKey}/lock`)
    ])
    if (!metaResponse.ok) throw new Error(`Field metadata request failed: ${metaResponse.status}`)
    if (!refResponse.ok) throw new Error(`Reference data request failed: ${refResponse.status}`)
    if (!detailResponse.ok) throw new Error(`Account request failed: ${detailResponse.status}`)

    fieldMetadata.value = await metaResponse.json()
    referenceData.value = await refResponse.json()
    detail.value = await detailResponse.json()
    lockStatus.value = lockResponse.ok ? await lockResponse.json() : null

    resetFormFromDetail()

    if (!lockStatus.value) {
      await acquireLock()
    }
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

function resetFormFromDetail() {
  form.value = {
    currentStageKey: detail.value.currentStageKey,
    currentStatusKey: detail.value.currentStatusKey,
    riskLevelKey: detail.value.riskLevelKey,
    accountTypeKey: detail.value.accountTypeKey,
    sorKey: detail.value.sorKey,
    ownerName: detail.value.ownerName,
    businessUnit: detail.value.businessUnit,
    targetRemediationDate: detail.value.targetRemediationDate?.slice(0, 10) ?? '',
    actualCompletionDate: detail.value.actualCompletionDate?.slice(0, 10) ?? '',
    notes: detail.value.notes
  }
  selectedExceptionKey.value = detail.value.exceptionKey ?? ''
}

async function acquireLock() {
  const response = await fetch(`/api/account-progress/${props.accountKey}/lock`, { method: 'POST' })
  if (response.ok) {
    lockStatus.value = await response.json()
    lockedByMe.value = true
    heartbeatTimer = setInterval(sendHeartbeat, 60000)
  } else if (response.status === 409) {
    lockStatus.value = await response.json()
    lockedByMe.value = false
  } else {
    error.value = `Could not acquire edit lock: ${response.status}`
  }
}

async function sendHeartbeat() {
  await fetch(`/api/account-progress/${props.accountKey}/lock/heartbeat`, { method: 'PUT' })
}

async function forceRelease() {
  const response = await fetch(`/api/account-progress/${props.accountKey}/lock/force-release`, { method: 'POST' })
  if (response.ok) {
    lockStatus.value = null
    await acquireLock()
  } else {
    error.value = `Force-release failed: ${response.status}`
  }
}

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const body = {
      currentStageKey: Number(form.value.currentStageKey),
      currentStatusKey: Number(form.value.currentStatusKey),
      riskLevelKey: form.value.riskLevelKey ? Number(form.value.riskLevelKey) : null,
      accountTypeKey: form.value.accountTypeKey ? Number(form.value.accountTypeKey) : null,
      sorKey: form.value.sorKey ? Number(form.value.sorKey) : null,
      ownerName: form.value.ownerName || null,
      businessUnit: form.value.businessUnit || null,
      targetRemediationDate: form.value.targetRemediationDate || null,
      actualCompletionDate: form.value.actualCompletionDate || null,
      notes: form.value.notes || null,
      reason: reason.value || null,
      exceptionKey: selectedExceptionKey.value ? Number(selectedExceptionKey.value) : null
    }
    const response = await fetch(`/api/account-progress/${props.accountKey}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) {
      if (response.status === 400) {
        const problem = await response.json()
        saveError.value = problem.detail || 'Validation failed.'
      } else if (response.status === 409) {
        saveError.value = 'This record is no longer locked by you -- reload the page.'
      } else {
        saveError.value = `Save failed: ${response.status}`
      }
      return
    }
    lockedByMe.value = false
    stopHeartbeat()
    const refreshed = await fetch(`/api/account-progress/${props.accountKey}`)
    detail.value = await refreshed.json()
    resetFormFromDetail()
    reason.value = ''
  } finally {
    saving.value = false
  }
}

async function cancelEdit() {
  await releaseLock()
  resetFormFromDetail()
}

async function releaseLock() {
  stopHeartbeat()
  if (lockedByMe.value) {
    await fetch(`/api/account-progress/${props.accountKey}/lock`, { method: 'DELETE' })
    lockedByMe.value = false
  }
}

function stopHeartbeat() {
  if (heartbeatTimer) {
    clearInterval(heartbeatTimer)
    heartbeatTimer = null
  }
}

onMounted(load)
onUnmounted(releaseLock)
</script>

<template>
  <div>
    <h1>Account Progress — {{ detail?.accountName ?? accountKey }}</h1>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">{{ error }}</p>

    <template v-else>
      <p v-if="lockStatus && !lockedByMe">
        Currently being edited by {{ lockStatus.lockedByName }} since {{ lockStatus.lockedAt }}.
        <button @click="forceRelease">Force Release Lock</button>
      </p>

      <form v-if="lockedByMe" @submit.prevent="save">
        <p v-if="saveError">{{ saveError }}</p>
        <p v-for="field in sortedFields" :key="field.fieldName">
          <label>
            {{ field.displayLabel }}<span v-if="field.isRequired"> *</span>:

            <select v-if="field.fieldType === 'Dropdown'" v-model="form[formKeyByFieldName[field.fieldName]]" :required="field.isRequired">
              <option value="">(none)</option>
              <option v-for="opt in optionsFor(field)" :key="opt.key" :value="opt.key">{{ opt.name }}</option>
            </select>

            <input v-else-if="field.fieldType === 'Date'" v-model="form[formKeyByFieldName[field.fieldName]]" type="date" />

            <textarea v-else-if="field.fieldType === 'TextArea'" v-model="form[formKeyByFieldName[field.fieldName]]"></textarea>

            <input v-else v-model="form[formKeyByFieldName[field.fieldName]]" type="text" />
          </label>
        </p>
        <div v-if="isRiskAccepted">
          <h3>Risk Exception</h3>
          <p>Status is Risk Accepted / Excluded -- link an existing Active exception for this account, or create one.</p>
          <p v-if="exceptionError">{{ exceptionError }}</p>
          <p>
            <label>
              Linked Exception:
              <select v-model="selectedExceptionKey" required>
                <option value="" disabled>Select an exception</option>
                <option v-for="ex in linkableExceptions" :key="ex.exceptionKey" :value="ex.exceptionKey">
                  {{ ex.exceptionID }} -- {{ ex.justification }}
                </option>
              </select>
            </label>
          </p>
          <button type="button" @click="showCreateExceptionForm = !showCreateExceptionForm">
            {{ showCreateExceptionForm ? 'Cancel New Exception' : '+ Create New Exception' }}
          </button>
          <div v-if="showCreateExceptionForm">
            <p><label>Justification: <textarea v-model="newException.justification" required></textarea></label></p>
            <p><label>Review Date: <input v-model="newException.reviewDate" type="date" required /></label></p>
            <p><label>External Ticket Reference: <input v-model="newException.externalTicketReference" type="text" /></label></p>
            <button type="button" :disabled="creatingException" @click="createInlineException">Create Exception</button>
          </div>
        </div>
        <p>
          <label>Reason (required only if regressing to an earlier stage): <input v-model="reason" type="text" /></label>
        </p>
        <button type="submit" :disabled="saving">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>

      <dl v-else>
        <dt>Stage</dt><dd>{{ detail.currentStageKey }}</dd>
        <dt>Status</dt><dd>{{ detail.currentStatusKey }}</dd>
        <dt>Owner</dt><dd>{{ detail.ownerName }}</dd>
        <dt>Notes</dt><dd>{{ detail.notes }}</dd>
      </dl>
    </template>
  </div>
</template>
