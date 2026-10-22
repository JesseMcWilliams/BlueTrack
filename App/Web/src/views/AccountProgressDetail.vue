<script setup>
// Field-metadata-driven edit form (Design_Interface-Extensibility.md) with
// pessimistic locking (D-50) and the two validation rules from D-51
// (enforced server-side; this form just surfaces whatever error comes back).
import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { useListStateStore } from '../stores/listState'
import { useRightsStore } from '../stores/rights'
import { formatDate } from '../utils/formatDate'
import { formatUtcDateTime } from '../utils/formatLocale'
import { confirmDelete } from '../composables/useConfirmDialog'
import { flash, savedMessage } from '../composables/useFlash'
import { decomNote } from '../utils/decomNote'
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'
import SafeAccessSection from '../components/SafeAccessSection.vue'

const { t } = useI18n()

const props = defineProps({ accountKey: { type: [String, Number], required: true } })
const router = useRouter()
const listState = useListStateStore()
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
  ApplicationKey: 'applicationKey',   // D-238
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
// UI review W2-3: the heartbeat found the lock gone (it expired, or a colleague forced it). The form and what was
// typed stay; Save waits until the lock is taken again.
const lockLost = ref(false)
const lockLostBy = ref(null)

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
const overrideSaved = ref(false) // UI review W2-8: the override saves on its own, so say so
const recalculating = ref(false)
const recalculateError = ref(null)

// D-132: the linked risk_exception's own detail (ExceptionID/Justification/
// ReviewDate/StatusName) -- detail.exceptionKey is only ever a pointer, so
// the read-only view needs its own fetch to show anything meaningful about
// it (GET .../{exceptionKey} has no policy restriction beyond [Authorize],
// same as every other read on this page).
const linkedException = ref(null)

async function loadLinkedException() {
  linkedException.value = null
  if (!detail.value?.exceptionKey) return
  const response = await fetch(`/api/risk-exceptions/${detail.value.exceptionKey}`)
  if (response.ok) linkedException.value = await response.json()
}

// D-132: read-only-view equivalent of the edit form's dropdown/date
// rendering -- same field-metadata list (sortedFields), same
// referenceData lookup (optionsFor), just resolved to display text instead
// of an <select>/<input>. Keeps the read-only view honest with whatever
// fields account_progress_field_metadata defines, rather than a hand-picked
// subset that silently drifts from the edit form over time.
function displayValueFor(field) {
  const value = detail.value?.[formKeyByFieldName[field.fieldName]]
  if (field.fieldType === 'Dropdown') {
    const match = optionsFor(field).find(opt => String(opt.key) === String(value))
    return match?.name ?? '—'
  }
  if (field.fieldType === 'Date') {
    return value ? formatDate(value) : '—'
  }
  return value || '—'
}

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
      recalculateError.value = t('pages.accountProgressDetail.recalculateFailed', { status: response.status })
      return
    }
    await refreshDetail()
  } finally {
    recalculating.value = false
  }
}

async function saveOverride() {
  overrideError.value = null
  overrideSaved.value = false
  // v-model.number leaves an emptied input as '' rather than null.
  const score = overrideScoreInput.value === '' || overrideScoreInput.value === undefined || Number.isNaN(overrideScoreInput.value)
    ? null
    : overrideScoreInput.value
  if (score !== null && !overrideReasonInput.value.trim()) {
    overrideError.value = t('pages.accountProgressDetail.reasonRequired')
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
      overrideError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    overrideReasonInput.value = ''
    await refreshDetail()
    overrideSaved.value = true
  } finally {
    overrideSaving.value = false
  }
}

let heartbeatTimer = null

const sortedFields = computed(() => [...fieldMetadata.value].sort((a, b) => a.displayOrder - b.displayOrder))

// D-198/D-291: a field with a RequiredPermission the user lacks is shown read-only (the save refuses a change to it).
function fieldLocked(field) {
  return !!field.requiredPermissionName && !rights.hasPermission(field.requiredPermissionName)
}

// Risk Exception wiring (Design_Risk-Exception-Tracking.md workflow steps
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
  { key: 'details', labelKey: 'pages.accountProgressDetail.tabDetails' },
  { key: 'risk-score', labelKey: 'pages.accountProgressDetail.tabRiskScore' },
  ...(isRiskAccepted.value ? [{ key: 'risk-exception', labelKey: 'pages.accountProgressDetail.tabRiskException' }] : [])
])

function onTabKeydown(event) {
  const currentIndex = tabs.value.findIndex(tab => tab.key === activeTab.value)
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
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
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
      exceptionError.value = t('pages.accountProgressDetail.couldNotCreateException', { status: response.status })
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
    if (!metaResponse.ok) throw new Error(t('pages.accountProgressDetail.fieldMetadataFailed', { status: metaResponse.status }))
    if (!refResponse.ok) throw new Error(t('pages.accountProgressDetail.referenceDataFailed', { status: refResponse.status }))
    if (!detailResponse.ok) throw new Error(t('pages.accountProgressDetail.accountRequestFailed', { status: detailResponse.status }))

    fieldMetadata.value = await metaResponse.json()
    referenceData.value = await refResponse.json()
    detail.value = await detailResponse.json()
    lockStatus.value = await readJsonOrDefault(lockResponse, null)
    applicationExceptions.value = await readJsonOrDefault(appExceptionsResponse, [])

    resetFormFromDetail()
    await loadLinkedException()

    // Don't attempt to acquire the edit lock for a user who can't edit
    // anyway -- that's a 403 from AcquireLock, not a real "someone else has
    // it" conflict, and shouldn't read as an error on a page someone is
    // legitimately just viewing. await, not assume: ensureLoaded() might
    // not have resolved yet even though App.vue also calls it (Vue mounts
    // children before parents).
    await rights.ensureLoaded()
    // D-177: also take over a lock this same user already holds -- another
    // tab, or an earlier visit that left without saving. AcquireLock grants
    // it to its own holder; previously the page never asked, so the user
    // saw "Currently being edited by <themselves>" until it expired.
    const heldByMe = lockStatus.value && lockStatus.value.lockedByUserKey === rights.userKey
    if ((!lockStatus.value || heldByMe) && rights.hasPermission('EditAccountProgress')) {
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
    applicationKey: detail.value.applicationKey,
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
    error.value = t('pages.accountProgressDetail.couldNotAcquireLock', { status: response.status })
  }
}

async function sendHeartbeat() {
  try {
    const response = await fetch(`/api/account-progress/${props.accountKey}/lock/heartbeat`, { method: 'PUT' })
    if (response.status === 409) {
      stopHeartbeat()
      lockedByMe.value = false
      lockStatus.value = null
      lockLost.value = true
    }
  } catch {
    // A dropped request is not a lost lock: the next beat tries again.
  }
}

// UI review W2-3: take the lock again without reloading, so the form keeps what was typed.
async function reacquireLock() {
  lockLostBy.value = null
  const response = await fetch(`/api/account-progress/${props.accountKey}/lock`, { method: 'POST' })
  if (response.ok) {
    lockStatus.value = await response.json()
    lockedByMe.value = true
    lockLost.value = false
    stopHeartbeat()
    heartbeatTimer = setInterval(sendHeartbeat, 60000)
  } else if (response.status === 409) {
    const holder = await response.json()
    lockStatus.value = holder
    lockLostBy.value = holder.lockedByName
  } else {
    saveError.value = t('pages.accountProgressDetail.couldNotAcquireLock', { status: response.status })
  }
}

async function forceRelease() {
  // UI review W2-4: this takes the account from a colleague who may be mid-edit; their unsaved changes are lost.
  const confirmed = await confirmDelete({
    title: t('pages.accountProgressDetail.forceReleaseTitle', { name: lockStatus.value.lockedByName }),
    details: [t('pages.accountProgressDetail.forceReleaseDetail', { name: lockStatus.value.lockedByName, since: formatUtcDateTime(lockStatus.value.lockedAt) })],
    warning: t('pages.accountProgressDetail.forceReleaseWarning', { name: lockStatus.value.lockedByName })
  }, t('pages.accountProgressDetail.forceRelease'))
  if (!confirmed) return
  const response = await fetch(`/api/account-progress/${props.accountKey}/lock/force-release`, { method: 'POST' })
  if (response.ok) {
    lockStatus.value = null
    await acquireLock()
  } else {
    error.value = t('pages.accountProgressDetail.forceReleaseFailed', { status: response.status })
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
      applicationKey: form.value.applicationKey ? Number(form.value.applicationKey) : null,
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
        saveError.value = problem.detail || t('pages.accountProgressDetail.validationFailed')
      } else if (response.status === 409) {
        saveError.value = t('pages.accountProgressDetail.noLongerLocked')
      } else {
        saveError.value = t('common.saveFailed', { status: response.status })
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
    // component is about to unmount. UI review W2-2: to the list as it was left (its filters, sort and page).
    flash(savedMessage(t, detail.value?.accountName))
    backToList()
  } finally {
    saving.value = false
  }
}

// D-125: Cancel now matches Save's own destination -- back to the Accounts
// list, not staying on this detail/summary page (confirmed directly; this
// was the one Cancel button in the app that didn't already do this).
async function cancelEdit() {
  await releaseLock()
  backToList()
}

function backToList() {
  router.push({ name: 'account-progress-list', query: listState.queryFor('account-progress-list') })
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

// D-185: delete / undelete this account in BlueTrack, always with a
// reason, and its delete/undelete history.
const deletionAction = ref(null) // 'delete' | 'undelete'
const deletionReason = ref('')
const deletionError = ref(null)
const deletionHistory = ref([])

async function loadDeletionHistory() {
  const response = await fetch(`/api/account-progress/${props.accountKey}/deletion-history`)
  if (response.ok) deletionHistory.value = await response.json()
}

async function applyDeletion() {
  deletionError.value = null
  const response = await fetch(`/api/account-progress/${deletionAction.value}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ accountKeys: [Number(props.accountKey)], reason: deletionReason.value })
  })
  const result = await response.json().catch(() => null)
  if (!response.ok) {
    deletionError.value = result?.detail ?? t('pages.accountProgressDetail.failed', { status: response.status })
    return
  }
  if (result.skipped.length) {
    deletionError.value = result.skipped[0].reason
    return
  }
  deletionAction.value = null
  deletionReason.value = ''
  await Promise.all([refreshDetail(), loadDeletionHistory()])
}

onMounted(async () => {
  await load()
  await loadDeletionHistory()
})
onUnmounted(releaseLock)
</script>

<template>
  <div>
    <h1>{{ t('pages.accountProgressDetail.title', { name: detail?.accountName ?? accountKey }) }}</h1>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ error }}</p>

    <template v-else>
      <div v-if="decomNote(detail)" class="decom-notice" role="note">
        <strong>{{ t('pages.accountProgressDetail.flaggedForDeletion') }}</strong> {{ decomNote(detail) }}
        <template v-if="!detail.isDeleted">{{ ' ' + t('pages.accountProgressDetail.notYetDeleted') }}</template>
      </div>
      <div v-if="detail.isDeleted" class="deleted-notice" role="note">
        <strong>{{ t('pages.accountProgressDetail.isDeleted') }}</strong>
        <template v-if="detail.deletedByName">{{ ' ' + t('pages.accountProgressDetail.deletedByOn', { name: detail.deletedByName, date: formatDate(detail.deletedAt), reason: detail.deletionReason }) }}</template>
        <template v-if="detail.isDeletedInSource && detail.isInIgnoredSafe">{{ ' ' + t('pages.accountProgressDetail.notImportedIgnoredSafe') }}</template>
        <template v-else-if="detail.isDeletedInSource && detail.isInInternalSafe">{{ ' ' + t('pages.accountProgressDetail.deletedInternalSafe') }}</template>
        <template v-else-if="detail.isDeletedInSource">{{ ' ' + t('pages.accountProgressDetail.deletedInCyberArk') }}</template>
      </div>
      <div v-if="rights.hasPermission('DeleteAccounts')" class="deletion-controls">
        <button v-if="!detail.deletedByName" type="button" @click="deletionAction = 'delete'">{{ t('pages.accountProgressDetail.deleteAccount') }}</button>
        <button v-else type="button" @click="deletionAction = 'undelete'">{{ t('pages.accountProgressDetail.undeleteAccount') }}</button>
        <form v-if="deletionAction" @submit.prevent="applyDeletion">
          <label class="field-label"><span class="field-label-text">{{ deletionAction === 'delete' ? t('pages.accountProgressDetail.reasonToDelete') : t('pages.accountProgressDetail.reasonToUndelete') }}</span> <input v-model="deletionReason" size="60" maxlength="1000" required /></label>
          <button type="submit" class="btn-primary" :disabled="!deletionReason.trim()">{{ deletionAction === 'delete' ? t('common.delete') : t('pages.accountProgressDetail.undelete') }}</button>
          <button type="button" @click="deletionAction = null">{{ t('common.cancel') }}</button>
          <p v-if="deletionError" role="alert">{{ deletionError }}</p>
        </form>
      </div>
      <details v-if="deletionHistory.length" class="deletion-history">
        <summary>{{ t('pages.accountProgressDetail.historyTitle', { count: formatCount(deletionHistory.length) }) }}</summary>
        <ul>
          <li v-for="h in deletionHistory" :key="`${h.performedAt}-${h.action}`">
            {{ t(h.action === 'Delete' ? 'pages.accountProgressDetail.historyDeleted' : 'pages.accountProgressDetail.historyUndeleted', { date: formatDate(h.performedAt), name: h.performedByName, reason: h.reason }) }}
          </li>
        </ul>
      </details>
      <div v-if="applicationExceptions.length > 0">
        <i18n-t v-for="ex in applicationExceptions" :key="ex.exceptionID" keypath="pages.accountProgressDetail.coveredByAppException" tag="p" scope="global">
          <template #id><strong>{{ ex.exceptionID }}</strong></template>
          <template #application>{{ ex.applicationName }}</template>
          <template #date>{{ formatDate(ex.reviewDate) }}</template>
        </i18n-t>
      </div>
      <!-- UI review W2-5: say why there is no edit form. -->
      <p v-if="rights.loaded && !rights.hasPermission('EditAccountProgress')" class="hint">{{ t('pages.accountProgressDetail.viewOnly') }}</p>
      <p v-if="lockStatus && !lockedByMe">
        {{ t('pages.accountProgressDetail.lockedBy', { name: lockStatus.lockedByName, since: formatUtcDateTime(lockStatus.lockedAt) }) }}
        <button v-if="rights.hasPermission('EditAccountProgress')" @click="forceRelease">{{ t('pages.accountProgressDetail.forceRelease') }}</button>
      </p>

      <div class="account-progress-identity">
        <p class="account-progress-identity-primary">
          <span class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.username') }}</strong> {{ detail.userName ?? '—' }}</span>
          <span class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.address') }}</strong> {{ detail.address ?? '—' }}</span>
        </p>
        <p class="account-progress-identity-secondary"><strong>{{ t('pages.accountProgressDetail.accountName') }}</strong> {{ detail.accountName }}</p>
        <!-- D-277: Self-Hosted Direct only. -->
        <p v-if="detail.createdTime || detail.lastCPMAction" class="account-progress-identity-secondary cpm-facts">
          <span v-if="detail.createdTime" class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.createdInCyberArk') }}</strong> {{ formatUtcDateTime(detail.createdTime) }}</span>
          <span v-if="detail.lastCPMAction" class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.lastCpmAction') }}</strong> {{ t('pages.accountProgressDetail.lastCpmActionValue', { action: detail.lastCPMAction, result: detail.lastCPMActionResult ?? '—', time: formatUtcDateTime(detail.lastCPMActionTime) }) }}</span>
        </p>
        <!-- D-288: CPM health, for both sources. -->
        <dl v-if="detail.cpmStatus || detail.autoManaged !== null && detail.autoManaged !== undefined || detail.lastVerifiedDate || detail.lastCPMModifiedDate" class="cpm-health">
          <template v-if="detail.cpmStatus">
            <dt>{{ t('pages.accountProgressDetail.cpmStatus') }}</dt>
            <dd :class="{ 'cpm-failing': detail.cpmStatus === 'failure' }">{{ detail.cpmStatus }}<template v-if="detail.cpmFailureSince">{{ ', ' + t('pages.accountProgressDetail.cpmFailingSince', { date: formatUtcDateTime(detail.cpmFailureSince) }) }}</template></dd>
          </template>
          <template v-if="detail.autoManaged !== null && detail.autoManaged !== undefined">
            <dt>{{ t('pages.accountProgressDetail.cpmManaged') }}</dt>
            <dd>{{ detail.manualReason ? t('pages.accountProgressDetail.cpmManagedReason', { value: detail.autoManaged ? t('common.yes') : t('common.no'), reason: detail.manualReason }) : (detail.autoManaged ? t('common.yes') : t('common.no')) }}</dd>
          </template>
          <template v-if="detail.lastVerifiedDate"><dt>{{ t('pages.accountProgressDetail.cpmLastVerified') }}</dt><dd>{{ formatDate(detail.lastVerifiedDate) }}</dd></template>
          <template v-if="detail.lastReconciledDate"><dt>{{ t('pages.accountProgressDetail.cpmLastReconciled') }}</dt><dd>{{ formatDate(detail.lastReconciledDate) }}</dd></template>
          <template v-if="detail.lastCPMModifiedDate"><dt>{{ t('pages.accountProgressDetail.cpmLastChanged') }}</dt><dd>{{ formatDate(detail.lastCPMModifiedDate) }}</dd></template>
          <template v-if="detail.requirePsm !== null && detail.requirePsm !== undefined"><dt>{{ t('pages.accountProgressDetail.cpmRequiresPsm') }}</dt><dd>{{ detail.requirePsm ? t('common.yes') : t('common.no') }}</dd></template>
        </dl>
        <!-- D-283: which Progress Rule moved the account to its stage. -->
        <p v-if="detail.lastAdvancedByRuleName" class="account-progress-identity-secondary moved-by-rule">{{ t('pages.accountProgressDetail.movedByRule', { rule: detail.lastAdvancedByRuleName, date: formatUtcDateTime(detail.lastAdvancedAt) }) }}</p>
        <!-- D-280: Self-Hosted Direct only, from PVWA's classic account details. -->
        <p v-if="detail.deviceType || detail.creationMethod || detail.linkedLogonAccount || detail.linkedReconcileAccount" class="account-progress-identity-secondary classic-facts">
          <span v-if="detail.deviceType" class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.deviceType') }}</strong> {{ detail.deviceType }}</span>
          <span v-if="detail.creationMethod" class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.creationMethod') }}</strong> {{ detail.creationMethod }}</span>
          <span v-if="detail.linkedLogonAccount" class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.linkedLogonAccount') }}</strong> <router-link v-if="detail.linkedLogonAccountKey" :to="{ name: 'account-progress-detail', params: { accountKey: detail.linkedLogonAccountKey } }">{{ detail.linkedLogonAccount }}</router-link><template v-else>{{ detail.linkedLogonAccount }}</template></span>
          <span v-if="detail.linkedReconcileAccount" class="account-progress-identity-item"><strong>{{ t('pages.accountProgressDetail.linkedReconcileAccount') }}</strong> <router-link v-if="detail.linkedReconcileAccountKey" :to="{ name: 'account-progress-detail', params: { accountKey: detail.linkedReconcileAccountKey } }">{{ detail.linkedReconcileAccount }}</router-link><template v-else>{{ detail.linkedReconcileAccount }}</template></span>
        </p>
      </div>

      <form v-if="lockedByMe || lockLost" @submit.prevent="save">
        <p v-if="lockLost" role="alert">
          {{ lockLostBy ? t('pages.accountProgressDetail.lockLostTaken', { name: lockLostBy }) : t('pages.accountProgressDetail.lockLost') }}
          <button type="button" @click="reacquireLock">{{ t('pages.accountProgressDetail.lockReacquire') }}</button>
        </p>
        <p v-if="saveError" role="alert">{{ saveError }}</p>

        <div role="tablist" class="account-progress-tabs" :aria-label="t('pages.accountProgressDetail.sectionsLabel')">
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
          >{{ t(tab.labelKey) }}</button>
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

              <select v-if="field.fieldType === 'Dropdown'" v-model="form[formKeyByFieldName[field.fieldName]]" :required="field.isRequired" :disabled="fieldLocked(field)">
                <option value="">{{ t('common.none') }}</option>
                <option v-for="opt in optionsFor(field)" :key="opt.key" :value="opt.key">{{ opt.name }}</option>
              </select>

              <input v-else-if="field.fieldType === 'Date'" v-model="form[formKeyByFieldName[field.fieldName]]" type="date" :disabled="fieldLocked(field)" />

              <textarea v-else-if="field.fieldType === 'TextArea'" v-model="form[formKeyByFieldName[field.fieldName]]" :disabled="fieldLocked(field)"></textarea>

              <input v-else v-model="form[formKeyByFieldName[field.fieldName]]" type="text" :disabled="fieldLocked(field)" />
            </label>
            <span v-if="fieldLocked(field)" class="hint">{{ t('pages.accountProgressDetail.fieldNeedsPermission', { permission: field.requiredPermissionName }) }}</span>
          </p>
        </div>

        <div
          v-show="activeTab === 'risk-score'"
          id="account-progress-panel-risk-score"
          role="tabpanel"
          aria-labelledby="account-progress-tab-risk-score"
        >
          <dl>
            <dt>{{ t('pages.accountProgressDetail.calculatedRisk') }}</dt><dd>{{ formatCount(detail.computedRiskScore) || t('pages.accountProgressDetail.notYetCalculated') }}</dd>
            <dt>{{ t('pages.accountProgressDetail.riskBand') }}</dt><dd>{{ detail.riskScoreBandName ?? '—' }}</dd>
            <dt>{{ t('pages.accountProgressDetail.effectiveRiskScore') }}</dt><dd>{{ formatCount(detail.effectiveRiskScore) || '—' }}</dd>
          </dl>
          <p v-if="recalculateError" role="alert">{{ recalculateError }}</p>
          <p>
            <button type="button" class="account-progress-recalculate" :disabled="recalculating" @click="recalculate">{{ recalculating ? t('pages.accountProgressDetail.recalculating') : t('pages.accountProgressDetail.recalculate') }}</button>
          </p>
          <p v-if="overrideError" role="alert">{{ overrideError }}</p>
          <p v-if="overrideSaved" role="status">{{ t('pages.accountProgressDetail.overrideSaved') }}</p>
          <p><small>{{ t('pages.accountProgressDetail.overrideHint') }}</small></p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressDetail.overrideScore') }}</span>
              <input v-model.number="overrideScoreInput" type="number" min="0" max="1000" />
            </label>
            <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressDetail.reason') }}</span>
              <input v-model="overrideReasonInput" type="text" />
            </label>
            <button type="button" :disabled="overrideSaving" @click="saveOverride">{{ t('pages.accountProgressDetail.saveOverride') }}</button>
          </p>
        </div>

        <div
          v-if="isRiskAccepted"
          v-show="activeTab === 'risk-exception'"
          id="account-progress-panel-risk-exception"
          role="tabpanel"
          aria-labelledby="account-progress-tab-risk-exception"
        >
          <p>{{ t('pages.accountProgressDetail.riskAcceptedHint') }}</p>
          <p v-if="exceptionError" role="alert">{{ exceptionError }}</p>
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('pages.accountProgressDetail.linkedException') }}</span>
              <select v-model="selectedExceptionKey" required>
                <option value="" disabled>{{ t('pages.accountProgressDetail.selectException') }}</option>
                <option v-for="ex in linkableExceptions" :key="ex.exceptionKey" :value="ex.exceptionKey">
                  {{ ex.exceptionID }} -- {{ ex.justification }}
                </option>
              </select>
            </label>
          </p>
          <button type="button" @click="showCreateExceptionForm = !showCreateExceptionForm">
            {{ showCreateExceptionForm ? t('pages.accountProgressDetail.cancelNewException') : t('pages.accountProgressDetail.createNewException') }}
          </button>
          <div v-if="showCreateExceptionForm">
            <p><label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressDetail.justification') }}</span> <textarea v-model="newException.justification" required></textarea></label></p>
            <p><label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressDetail.reviewDate') }}</span> <input v-model="newException.reviewDate" type="date" required /></label></p>
            <p><label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressDetail.externalTicketReference') }}</span> <input v-model="newException.externalTicketReference" type="text" /></label></p>
            <button type="button" class="btn-primary" :disabled="creatingException" @click="createInlineException">{{ t('pages.accountProgressDetail.createException') }}</button>
          </div>
        </div>

        <p>
          <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressDetail.regressionReason') }}</span> <input v-model="reason" type="text" /></label>
        </p>
        <button type="submit" class="btn-primary" :disabled="saving || lockLost">{{ t('common.save') }}</button>
        <button type="button" @click="cancelEdit">{{ t('common.cancel') }}</button>
      </form>

      <dl v-else>
        <template v-for="field in sortedFields" :key="field.fieldName">
          <dt>{{ field.displayLabel }}</dt>
          <dd>{{ displayValueFor(field) }}</dd>
        </template>
        <dt>{{ t('pages.accountProgressDetail.lastUpdated') }}</dt><dd>{{ formatDate(detail.lastUpdated) }}</dd>
        <dt>{{ t('pages.accountProgressDetail.calculatedRisk') }}</dt><dd>{{ formatCount(detail.computedRiskScore) || t('pages.accountProgressDetail.notYetCalculated') }}</dd>
        <dt>{{ t('pages.accountProgressDetail.riskBand') }}</dt><dd>{{ detail.riskScoreBandName ?? '—' }}</dd>
        <dt>{{ t('pages.accountProgressDetail.effectiveRiskScore') }}</dt><dd>{{ formatCount(detail.effectiveRiskScore) || '—' }}</dd>
        <template v-if="detail.overrideRiskScore !== null">
          <dt>{{ t('pages.accountProgressDetail.overrideRiskScore') }}</dt><dd>{{ formatCount(detail.overrideRiskScore) }}</dd>
        </template>
        <template v-if="linkedException">
          <dt>{{ t('pages.accountProgressDetail.riskException') }}</dt>
          <dd>{{ t('pages.accountProgressDetail.linkedExceptionValue', { id: linkedException.exceptionID, status: linkedException.statusName, justification: linkedException.justification, date: formatDate(linkedException.reviewDate) }) }}</dd>
        </template>
      </dl>

      <!-- D-281/D-282 -->
      <SafeAccessSection :account-key="accountKey" />
    </template>
  </div>
</template>

<style scoped>
.deleted-notice, .decom-notice {
  border: 2px solid currentColor;
  padding: 0.5rem 0.75rem;
  margin: 0.5rem 0;
}
.deletion-controls, .deletion-history {
  margin: 0.5rem 0;
}
.account-progress-identity {
  margin-bottom: var(--space-3);
}
.cpm-health {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.15rem 0.75rem;
  margin: 0.5rem 0;
}
.cpm-health dt {
  font-weight: bold;
}
.cpm-health dd {
  margin: 0;
}
.cpm-failing {
  color: var(--color-error-text);
  font-weight: bold;
}
.account-progress-identity-primary {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-3);
  margin: 0;
}
.account-progress-identity-item + .account-progress-identity-item {
  padding-left: var(--space-3);
  border-left: 1px solid var(--color-border);
}
.account-progress-identity-secondary {
  margin: var(--space-1) 0 0;
  font-size: 0.875em;
}
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
