<script setup>
// Create: POST /api/risk-exceptions. Edit: GET /api/risk-exceptions/{key}
// plus the extend-review/revoke actions (RiskExceptionsController). All
// three require ApproveExceptions (D-07) on the API side.
//
// NOT built yet: an account search/picker -- fact_account has 2,400+ rows,
// too many for a plain dropdown, and D-42's real filter/search UI isn't
// built anywhere in this app yet. Account scope takes a raw AccountKey for
// now; Application scope gets a real dropdown since dim_application is a
// small curated list (ApplicationRepository.GetAllAsync loads it in full).
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { formatDate } from '../utils/formatDate'
import { confirmDelete } from '../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'
import Combobox from '../components/Combobox.vue'

const { t } = useI18n()

const props = defineProps({ exceptionKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.exceptionKey !== null && props.exceptionKey !== undefined)

const loading = ref(isEditMode.value)
const saving = ref(false)
// UI review W3-1: a load failure has nothing to show but the error; a failed save, extend or revoke keeps the form or
// the detail on screen (nothing to retype) with its error beside it. `notice` confirms what worked (W3-3).
const loadError = ref(null)
const error = ref(null)
const notice = ref(null)
const applications = ref([])

const detail = ref(null) // populated in edit mode
// UI review W3-4: for an application-scoped exception, the accounts it covers (the first 200) and how many there are
const covered = ref({ accounts: [], total: 0, error: null })

const scopeType = ref('Account')
const accountChoice = ref(null) // UI review W3-2: picked by typing, not by a raw AccountKey
const applicationKey = ref('')
const justification = ref('')
const reviewDate = ref('')
const externalTicketReference = ref('')

const newReviewDate = ref('')

async function loadCovered() {
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}/accounts`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    const accounts = await response.json()
    covered.value = { accounts, total: Number(response.headers.get('X-Total-Count') ?? accounts.length), error: null }
  } catch (err) {
    covered.value = { accounts: [], total: 0, error: err.message }
  }
}

async function loadDetail() {
  loading.value = true
  loadError.value = null
  error.value = null
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}`)
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    detail.value = await response.json()
    covered.value = { accounts: [], total: 0, error: null }
    if (detail.value.applicationKey) await loadCovered()
    newReviewDate.value = detail.value.reviewDate?.slice(0, 10) ?? ''
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const appsResponse = await fetch('/api/applications')
    if (appsResponse.ok) {
      applications.value = await appsResponse.json()
    }
  } catch {
    // Non-fatal for create mode if this fails -- the account-scoped path still works.
  }

  if (isEditMode.value) {
    await loadDetail()
  }
})

// createException() navigates create -> edit via router.push on this same
// route component (only the param changes, no remount), so onMounted alone
// never re-fires -- watch the prop directly to load the new detail.
watch(() => props.exceptionKey, (newKey) => {
  if (newKey !== null && newKey !== undefined) {
    loadDetail()
  }
})

// UI review W3-2: the account is found by typing part of its user name, account name or address.
async function searchAccounts(text) {
  const response = await fetch(`/api/account-progress?search=${encodeURIComponent(text)}&pageSize=10`)
  if (!response.ok) throw new Error(t('pages.riskExceptionEdit.accountSearchFailed', { status: response.status }))
  return response.json()
}

function accountLabel(account) {
  const name = account.userName || account.accountName
  return account.address ? `${name} (${account.address})` : name
}

async function createException() {
  error.value = null
  notice.value = null
  if (scopeType.value === 'Account' && !accountChoice.value) {
    error.value = t('pages.riskExceptionEdit.chooseAccount')
    return
  }
  saving.value = true
  try {
    const body = {
      accountKey: scopeType.value === 'Account' ? accountChoice.value.accountKey : null,
      applicationKey: scopeType.value === 'Application' ? Number(applicationKey.value) : null,
      justification: justification.value,
      reviewDate: reviewDate.value,
      externalTicketReference: externalTicketReference.value || null
    }
    const response = await fetch('/api/risk-exceptions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) {
      const problem = response.status === 403 ? null : await response.json().catch(() => null)
      throw new Error(response.status === 403
        ? t('pages.riskExceptionEdit.noPermission')
        : problem?.detail ?? t('common.requestFailed', { status: response.status }))
    }
    const created = await response.json()
    router.push({ name: 'risk-exception-edit', params: { exceptionKey: created.exceptionKey } })
  } catch (err) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}

async function extendReview() {
  error.value = null
  notice.value = null
  saving.value = true
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}/extend-review`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ newReviewDate: newReviewDate.value })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      throw new Error(problem?.detail ?? t('common.requestFailed', { status: response.status }))
    }
    detail.value.reviewDate = newReviewDate.value
    notice.value = t('pages.riskExceptionEdit.reviewExtended', { date: formatDate(newReviewDate.value) })
  } catch (err) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}

// D-190: revoking always needs a reason, recorded in the audit log.
const revokeReason = ref('')

async function revoke() {
  error.value = null
  notice.value = null
  // UI review W3-3: revoking ends the exception and its accounts lose the cover at the next evaluation; ask first.
  const confirmed = await confirmDelete({
    title: t('pages.riskExceptionEdit.revokeConfirmTitle', { id: detail.value?.exceptionID ?? props.exceptionKey }),
    details: [t('pages.riskExceptionEdit.revokeConfirmReason', { reason: revokeReason.value.trim() })],
    warning: t('pages.riskExceptionEdit.revokeConfirmWarning')
  }, t('pages.riskExceptionEdit.revokeException'))
  if (!confirmed) return
  saving.value = true
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}/revoke`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ reason: revokeReason.value })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      throw new Error(problem?.detail ?? t('common.requestFailed', { status: response.status }))
    }
    detail.value.statusName = 'Revoked'
    notice.value = t('pages.riskExceptionEdit.revoked')
  } catch (err) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <h1>{{ isEditMode ? t('pages.riskExceptionEdit.titleEdit', { id: detail?.exceptionID ?? exceptionKey }) : t('pages.riskExceptionEdit.titleNew') }}</h1>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else-if="isEditMode && detail">
      <p v-if="error" role="alert">{{ error }}</p>
      <p v-if="notice" role="status">{{ notice }}</p>
      <dl>
        <dt>{{ t('pages.riskExceptionEdit.scope') }}</dt>
        <!-- UI review W3-4: the account or application by name, the account as a link to it; the key is the fallback. -->
        <dd v-if="detail.accountKey">
          <router-link v-if="detail.accountName" :to="{ name: 'account-progress-detail', params: { accountKey: detail.accountKey } }">{{ detail.accountAddress ? `${detail.accountName} (${detail.accountAddress})` : detail.accountName }}</router-link>
          <template v-else>{{ t('pages.riskExceptionEdit.scopeAccount', { key: detail.accountKey }) }}</template>
        </dd>
        <dd v-else>{{ detail.applicationName ? t('pages.riskExceptionEdit.scopeApplicationNamed', { name: detail.applicationName }) : t('pages.riskExceptionEdit.scopeApplication', { key: detail.applicationKey }) }}</dd>
        <template v-if="detail.applicationKey">
          <dt>{{ t('pages.riskExceptionEdit.coveredHeading') }}</dt>
          <dd>
            <p v-if="covered.error" role="alert">{{ t('pages.riskExceptionEdit.coveredFailed', { error: covered.error }) }}</p>
            <template v-else-if="covered.total === 0">{{ t('pages.riskExceptionEdit.coveredNone') }}</template>
            <template v-else>
              <span>{{ covered.total > covered.accounts.length ? t('pages.riskExceptionEdit.coveredFirst', { shown: covered.accounts.length, total: covered.total }) : t('pages.riskExceptionEdit.coveredAll', { total: covered.total }) }}</span>
              <ul class="covered-accounts">
                <li v-for="account in covered.accounts" :key="account.accountKey">
                  <router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.userName || account.accountName }}</router-link>
                  <span v-if="account.address">{{ ' (' + account.address + ')' }}</span>
                  <span v-if="account.safeName">{{ ' ' + t('pages.riskExceptionEdit.coveredSafe', { safe: account.safeName }) }}</span>
                </li>
              </ul>
            </template>
          </dd>
        </template>
        <dt>{{ t('pages.riskExceptionEdit.justification') }}</dt>
        <dd>{{ detail.justification }}</dd>
        <dt>{{ t('pages.riskExceptionEdit.approvalDate') }}</dt>
        <dd>{{ formatDate(detail.approvalDate) }}</dd>
        <dt>{{ t('pages.riskExceptionEdit.approvedBy') }}</dt>
        <dd>{{ detail.approvedByName }}</dd>
        <dt>{{ t('pages.riskExceptionEdit.reviewDate') }}</dt>
        <dd>{{ formatDate(detail.reviewDate) }}</dd>
        <dt>{{ t('pages.riskExceptionEdit.status') }}</dt>
        <dd>{{ detail.statusName }}</dd>
        <dt>{{ t('pages.riskExceptionEdit.externalTicket') }}</dt>
        <dd>{{ detail.externalTicketReference }}</dd>
        <template v-if="detail.sourceTool">
          <dt>{{ t('pages.riskExceptionEdit.source') }}</dt>
          <dd>
            <a v-if="detail.sourceUrl" :href="detail.sourceUrl" target="_blank" rel="noopener noreferrer">{{ detail.sourceTool }} {{ detail.sourceExceptionId }}</a>
            <template v-else>{{ detail.sourceTool }} {{ detail.sourceExceptionId }}</template>
          </dd>
          <dt>{{ t('pages.riskExceptionEdit.imported') }}</dt>
          <dd>{{ t('pages.riskExceptionEdit.importedBy', { date: formatDate(detail.importedDate), name: detail.importedByName }) }}</dd>
        </template>
      </dl>

      <template v-if="detail.statusName === 'Active'">
        <h3>{{ t('pages.riskExceptionEdit.reapproveHeading') }}</h3>
        <input v-model="newReviewDate" type="date" />
        <button class="btn-primary" :disabled="saving" @click="extendReview">{{ t('pages.riskExceptionEdit.extendReviewDate') }}</button>

        <h3>{{ t('pages.riskExceptionEdit.revokeHeading') }}</h3>
        <p><label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionEdit.reasonToRevoke') }}</span> <input v-model="revokeReason" size="60" maxlength="1000" /></label></p>
        <button :disabled="saving || !revokeReason.trim()" @click="revoke">{{ t('pages.riskExceptionEdit.revokeException') }}</button>
      </template>
    </template>

    <template v-else>
      <form @submit.prevent="createException">
        <p v-if="error" role="alert">{{ error }}</p>
        <p>
          <label><input v-model="scopeType" type="radio" value="Account" /> {{ t('pages.riskExceptionEdit.account') }}</label>
          <label><input v-model="scopeType" type="radio" value="Application" /> {{ t('pages.riskExceptionEdit.application') }}</label>
        </p>
        <p v-if="scopeType === 'Account'">
          <span class="field-label-text">{{ t('pages.riskExceptionEdit.accountLabel') }}</span>
          <Combobox
            v-model="accountChoice"
            :label="t('pages.riskExceptionEdit.accountLabel')"
            :search="searchAccounts"
            :item-key="account => account.accountKey"
            :item-label="accountLabel"
            :placeholder="t('pages.riskExceptionEdit.accountPlaceholder')"
          >
            <template #option="{ item: account }">
              <strong>{{ account.userName || account.accountName }}</strong>
              <span v-if="account.address"> ({{ account.address }})</span>
              <br /><small>{{ account.stageName }}</small>
            </template>
          </Combobox>
        </p>
        <p v-else>
          <label class="field-label">
            <span class="field-label-text">{{ t('pages.riskExceptionEdit.applicationLabel') }}</span>
            <select v-model="applicationKey" required>
              <option value="" disabled>{{ t('pages.riskExceptionEdit.selectApplication') }}</option>
              <option v-for="app in applications" :key="app.applicationKey" :value="app.applicationKey">
                {{ app.applicationName }}
              </option>
            </select>
          </label>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionEdit.justificationLabel') }}</span> <textarea v-model="justification" required></textarea></label>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionEdit.reviewDateLabel') }}</span> <input v-model="reviewDate" type="date" required /></label>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('pages.riskExceptionEdit.externalTicketReference') }}</span> <input v-model="externalTicketReference" type="text" /></label>
        </p>
        <button type="submit" class="btn-primary" :disabled="saving">{{ t('pages.riskExceptionEdit.createException') }}</button>
      </form>
    </template>
  </div>
</template>
