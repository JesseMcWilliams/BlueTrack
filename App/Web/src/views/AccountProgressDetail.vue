<script setup>
// Field-metadata-driven edit form (Design_Interface_Extensibility.md) with
// pessimistic locking (D-50) and the two validation rules from D-51
// (enforced server-side; this form just surfaces whatever error comes back).
import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { useRightsStore } from '../stores/rights'
import { formatDate } from '../utils/formatDate'

const props = defineProps({ accountKey: { type: [String, Number], required: true } })
const router = useRouter()
const rights = useRightsStore()

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

// D-81: Active application-scoped exceptions covering this account,
// computed live -- independent of detail.exceptionKey, which only ever
// holds the account-scoped pointer (D-77). Informational only here; not
// editable, since there's nothing account-scoped editing can do about an
// application-scoped exception.
const applicationExceptions = ref([])

const loading = ref(true)
const error = ref(null)
const saveError = ref(null)
const saving = ref(false)

// D-127: moved here from the Account Progress list's own inline "Edit
// Override" (removed) -- Calculated Risk/Risk Band are always read-only
// (system-calculated); Override Risk Score is the one editable value,
// saved independently via its own existing endpoint (a Reason is required
// only when setting a value, mirroring web.risk_exception.Justification's
// own precedent, not required to clear one back to null) -- same
// validation this used to enforce on the list page, unchanged.
const overrideScoreInput = ref(null)
const overrideReasonInput = ref('')
const overrideError = ref(null)
const overrideSaving = ref(false)
const recalculating = ref(false)
const recalculateError = ref(null)

async function refreshDetail() {
  const detailResponse = await fetch(`/api/account-progress/${props.accountKey}`)
  if (detailResponse.ok) {
    detail.value = await detailResponse.json()
    overrideScoreInput.value = detail.value.overrideRiskScore ?? null
  }
}

// D-131: recalculates just this one account's Calculated Risk right now
// (usp_RecalculateRiskScoreForAccount) -- the bulk "Recalculate Now" on
// the Risk Score report only touches accounts already marked stale.
async function recalculate() {
  recalculateError.value = null
  recalculating.value = true
  try {
    const response = await fetch(`/api/account-progress/${props.accountKey}/recalculate-risk-score`, { method: 'POST' })
    if (!response.ok) {
      recalculateError.value = `Recalculate failed: ${response.status}`
      return
    }
    await refreshDetail()
  } finally {
    recalculating.value = false
  }
}

async function saveOverride() {
  overrideError.value = null
  // v-model.number leaves an emptied input as '' rather than null.
  const score = overrideScoreInput.value === '' || overrideScoreInput.value === undefined || Number.isNaN(overrideScoreInput.value)
    ? null
    : overrideScoreInput.value
  if (score !== null && !overrideReasonInput.value.trim()) {
    overrideError.value = 'A Reason is required when setting an override.'
    return
  }
  overrideSaving.value = true
  try {
    const response = await fetch(`/api/account-progress/${props.accountKey}/risk-score-override`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ overrideRiskScore: score, reason: overrideReasonInput.value || null })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      overrideError.value = problem?.detail ?? `Save failed: ${response.status}`
      return
    }
    overrideReasonInput.value = ''
    await refreshDetail()
  } finally {
    overrideSaving.value = false
  }
}

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
  } else if (activeTab.value === 'risk-exception') {
    // The tab this user was on just stopped existing (status moved away
    // from Risk Accepted / Excluded) -- fall back rather than leave the
    // panel showing content for a now-hidden tab.
    activeTab.value = 'details'
  }
})

// D-130: this form had grown into one long scroll (main fields, Risk
// Score, Risk Exception) -- split into tabs, confirmed directly, following
// the ARIA APG Tabs pattern (https://www.w3.org/WAI/ARIA/apg/patterns/tabs/)
// this app already uses for its sortable-table headers (D-92) elsewhere.
// Automatic activation model: arrow keys move focus AND switch the active
// panel immediately, matching that same precedent's own choice for a
// small, fixed set of options. The Reason field and Save/Cancel buttons
// stay outside the tabs entirely -- they apply to the whole record, not
// to whichever tab happens to be open.
const activeTab = ref('details')
const tabs = computed(() => [
  { key: 'details', label: 'Details' },
  { key: 'risk-score', label: 'Risk Score' },
  ...(isRiskAccepted.value ? [{ key: 'risk-exception', label: 'Risk Exception' }] : [])
])

function onTabKeydown(event) {
  const currentIndex = tabs.value.findIndex(t => t.key === activeTab.value)
  let targetIndex = null
  if (event.key === 'ArrowRight') targetIndex = (currentIndex + 1) % tabs.value.length
  else if (event.key === 'ArrowLeft') targetIndex = (currentIndex - 1 + tabs.value.length) % tabs.value.length
  else if (event.key === 'Home') targetIndex = 0
  else if (event.key === 'End') targetIndex = tabs.value.length - 1
  else return

  event.preventDefault()
  activeTab.value = tabs.value[targetIndex].key
  nextTick(() => document.getElementById(`account-progress-tab-${tabs.value[targetIndex].key}`)?.focus())
}

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

// GetLockStatus/GetApplicationExceptions return a genuinely empty 200 body
// (not literal "null"/"[]") when there's nothing to report -- .json() throws
// "Unexpected end of JSON input" on that, so read as text first.
async function readJsonOrDefault(response, defaultValue) {
  if (!response.ok) return defaultValue
  const text = await response.text()
  return text ? JSON.parse(text) : defaultValue
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const [metaResponse, refResponse, detailResponse, lockResponse, appExceptionsResponse] = await Promise.all([
      fetch('/api/account-progress/field-metadata'),
      fetch('/api/account-progress/reference-data'),
      fetch(`/api/account-progress/${props.accountKey}`),
      fetch(`/api/account-progress/${props.accountKey}/lock`),
      fetch(`/api/account-progress/${props.accountKey}/application-exceptions`)
    ])
    if (!metaResponse.ok) throw new Error(`Field metadata request failed: ${metaResponse.status}`)
    if (!refResponse.ok) throw new Error(`Reference data request failed: ${refResponse.status}`)
    if (!detailResponse.ok) throw new Error(`Account request failed: ${detailResponse.status}`)

    fieldMetadata.value = await metaResponse.json()
    referenceData.value = await refResponse.json()
    detail.value = await detailResponse.json()
    lockStatus.value = await readJsonOrDefault(lockResponse, null)
    applicationExceptions.value = await readJsonOrDefault(appExceptionsResponse, [])

    resetFormFromDetail()

    // Don't attempt to acquire the edit lock for a user who can't edit
    // anyway -- that's a 403 from AcquireLock, not a real "someone else has
    // it" conflict, and shouldn't read as an error on a page someone is
    // legitimately just viewing. await, not assume: ensureLoaded() might
    // not have resolved yet even though App.vue also calls it (Vue mounts
    // children before parents).
    await rights.ensureLoaded()
    if (!lockStatus.value && rights.hasPermission('EditAccountProgress')) {
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
  overrideScoreInput.value = detail.value.overrideRiskScore ?? null
  overrideReasonInput.value = ''
  overrideError.value = null
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
    // The server released the lock as part of a successful save
    // (AccountProgressController.Update) -- lockStatus must be cleared to
    // match, or the stale (still-truthy, still-mine) object left over from
    // before the save makes the "Currently being edited by <your own
    // name>" banner render right alongside the read-only view if the user
    // ever navigates back here, reading as an error even though the save
    // succeeded (found 2026-09-06). forceRelease() already got this right;
    // releaseLock() below did not.
    lockStatus.value = null
    lockedByMe.value = false
    stopHeartbeat()
    // A save returns to the Accounts list (2026-09-06, user-requested) --
    // no need to refetch/reset this page's own form first, since the
    // component is about to unmount.
    router.push({ name: 'account-progress-list' })
  } finally {
    saving.value = false
  }
}

// D-125: Cancel now matches Save's own destination -- back to the Accounts
// list, not staying on this detail/summary page (confirmed directly; this
// was the one Cancel button in the app that didn't already do this).
async function cancelEdit() {
  await releaseLock()
  router.push({ name: 'account-progress-list' })
}

async function releaseLock() {
  stopHeartbeat()
  if (lockedByMe.value) {
    await fetch(`/api/account-progress/${props.accountKey}/lock`, { method: 'DELETE' })
    lockStatus.value = null
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
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">{{ error }}</p>

    <template v-else>
      <div v-if="applicationExceptions.length > 0">
        <p v-for="ex in applicationExceptions" :key="ex.exceptionID">
          Covered by application-scoped exception <strong>{{ ex.exceptionID }}</strong> ({{ ex.applicationName }}), reviewed by {{ formatDate(ex.reviewDate) }}.
        </p>
      </div>
      <p v-if="lockStatus && !lockedByMe">
        Currently being edited by {{ lockStatus.lockedByName }} since {{ lockStatus.lockedAt }}.
        <button v-if="rights.hasPermission('EditAccountProgress')" @click="forceRelease">Force Release Lock</button>
      </p>

      <form v-if="lockedByMe" @submit.prevent="save">
        <p v-if="saveError" role="alert">{{ saveError }}</p>

        <div role="tablist" class="account-progress-tabs" aria-label="Account Progress sections">
          <button
            v-for="tab in tabs"
            :key="tab.key"
            :id="`account-progress-tab-${tab.key}`"
            role="tab"
            type="button"
            class="account-progress-tab"
            :class="{ 'account-progress-tab--active': activeTab === tab.key }"
            :aria-selected="activeTab === tab.key"
            :aria-controls="`account-progress-panel-${tab.key}`"
            :tabindex="activeTab === tab.key ? 0 : -1"
            @click="activeTab = tab.key"
            @keydown="onTabKeydown"
          >{{ tab.label }}</button>
        </div>

        <div
          v-show="activeTab === 'details'"
          id="account-progress-panel-details"
          role="tabpanel"
          aria-labelledby="account-progress-tab-details"
        >
          <p v-for="field in sortedFields" :key="field.fieldName">
            <label class="field-label">
              <span class="field-label-text">{{ field.displayLabel }}<span v-if="field.isRequired"> *</span>:</span>

              <select v-if="field.fieldType === 'Dropdown'" v-model="form[formKeyByFieldName[field.fieldName]]" :required="field.isRequired">
                <option value="">(none)</option>
                <option v-for="opt in optionsFor(field)" :key="opt.key" :value="opt.key">{{ opt.name }}</option>
              </select>

              <input v-else-if="field.fieldType === 'Date'" v-model="form[formKeyByFieldName[field.fieldName]]" type="date" />

              <textarea v-else-if="field.fieldType === 'TextArea'" v-model="form[formKeyByFieldName[field.fieldName]]"></textarea>

              <input v-else v-model="form[formKeyByFieldName[field.fieldName]]" type="text" />
            </label>
          </p>
        </div>

        <div
          v-show="activeTab === 'risk-score'"
          id="account-progress-panel-risk-score"
          role="tabpanel"
          aria-labelledby="account-progress-tab-risk-score"
        >
          <dl>
            <dt>Calculated Risk</dt><dd>{{ detail.computedRiskScore ?? '(not yet calculated)' }}</dd>
            <dt>Risk Band</dt><dd>{{ detail.riskScoreBandName ?? '—' }}</dd>
          </dl>
          <p v-if="recalculateError" role="alert">{{ recalculateError }}</p>
          <p>
            <button type="button" class="account-progress-recalculate" :disabled="recalculating" @click="recalculate">{{ recalculating ? 'Recalculating…' : 'Recalculate' }}</button>
          </p>
          <p v-if="overrideError" role="alert">{{ overrideError }}</p>
          <p>
            <label class="field-label"><span class="field-label-text">Override Score (0-1000, blank clears it):</span>
              <input v-model.number="overrideScoreInput" type="number" min="0" max="1000" />
            </label>
            <label class="field-label"><span class="field-label-text">Reason:</span>
              <input v-model="overrideReasonInput" type="text" />
            </label>
            <button type="button" :disabled="overrideSaving" @click="saveOverride">Save Override</button>
          </p>
        </div>

        <div
          v-if="isRiskAccepted"
          v-show="activeTab === 'risk-exception'"
          id="account-progress-panel-risk-exception"
          role="tabpanel"
          aria-labelledby="account-progress-tab-risk-exception"
        >
          <p>Status is Risk Accepted / Excluded -- link an existing Active exception for this account, or create one.</p>
          <p v-if="exceptionError" role="alert">{{ exceptionError }}</p>
          <p>
            <label class="field-label">
              <span class="field-label-text">Linked Exception:</span>
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
            <p><label class="field-label"><span class="field-label-text">Justification:</span> <textarea v-model="newException.justification" required></textarea></label></p>
            <p><label class="field-label"><span class="field-label-text">Review Date:</span> <input v-model="newException.reviewDate" type="date" required /></label></p>
            <p><label class="field-label"><span class="field-label-text">External Ticket Reference:</span> <input v-model="newException.externalTicketReference" type="text" /></label></p>
            <button type="button" class="btn-primary" :disabled="creatingException" @click="createInlineException">Create Exception</button>
          </div>
        </div>

        <p>
          <label class="field-label"><span class="field-label-text">Reason (required only if regressing to an earlier stage):</span> <input v-model="reason" type="text" /></label>
        </p>
        <button type="submit" class="btn-primary" :disabled="saving">Save</button>
        <button type="button" @click="cancelEdit">Cancel</button>
      </form>

      <dl v-else>
        <dt>Stage</dt><dd>{{ detail.currentStageKey }}</dd>
        <dt>Status</dt><dd>{{ detail.currentStatusKey }}</dd>
        <dt>Owner</dt><dd>{{ detail.ownerName }}</dd>
        <dt>Notes</dt><dd>{{ detail.notes }}</dd>
        <dt>Calculated Risk</dt><dd>{{ detail.computedRiskScore ?? '(not yet calculated)' }}</dd>
        <dt>Risk Band</dt><dd>{{ detail.riskScoreBandName ?? '—' }}</dd>
      </dl>
    </template>
  </div>
</template>

<style scoped>
.account-progress-tabs {
  display: flex;
  gap: var(--space-1);
  border-bottom: 1px solid var(--color-border);
  margin-bottom: var(--space-3);
}
.account-progress-tab {
  border: none;
  border-bottom: 3px solid transparent;
  border-radius: 0;
  background: transparent;
  margin-right: 0;
  margin-bottom: -1px;
}
.account-progress-tab--active {
  border-bottom-color: var(--color-link);
  font-weight: 700;
}
</style>
