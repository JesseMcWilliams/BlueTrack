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

const props = defineProps({ exceptionKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.exceptionKey !== null && props.exceptionKey !== undefined)

const loading = ref(isEditMode.value)
const saving = ref(false)
const error = ref(null)
const applications = ref([])

const detail = ref(null) // populated in edit mode

const scopeType = ref('Account')
const accountKey = ref('')
const applicationKey = ref('')
const justification = ref('')
const reviewDate = ref('')
const externalTicketReference = ref('')

const newReviewDate = ref('')

async function loadDetail() {
  loading.value = true
  error.value = null
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}`)
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    detail.value = await response.json()
    newReviewDate.value = detail.value.reviewDate?.slice(0, 10) ?? ''
  } catch (err) {
    error.value = err.message
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

async function createException() {
  error.value = null
  saving.value = true
  try {
    const body = {
      accountKey: scopeType.value === 'Account' ? Number(accountKey.value) : null,
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
      throw new Error(response.status === 403
        ? 'You do not have the ApproveExceptions permission.'
        : `Request failed: ${response.status}`)
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
  saving.value = true
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}/extend-review`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ newReviewDate: newReviewDate.value })
    })
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    detail.value.reviewDate = newReviewDate.value
  } catch (err) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}

async function revoke() {
  error.value = null
  saving.value = true
  try {
    const response = await fetch(`/api/risk-exceptions/${props.exceptionKey}/revoke`, { method: 'PUT' })
    if (!response.ok) {
      throw new Error(`Request failed: ${response.status}`)
    }
    detail.value.statusName = 'Revoked'
  } catch (err) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <h1>{{ isEditMode ? `Exception — ${detail?.exceptionID ?? exceptionKey}` : 'New Exception' }}</h1>
    <p v-if="loading">Loading...</p>
    <p v-else-if="error">{{ error }}</p>

    <template v-else-if="isEditMode && detail">
      <dl>
        <dt>Scope</dt>
        <dd>{{ detail.accountKey ? `Account #${detail.accountKey}` : `Application #${detail.applicationKey}` }}</dd>
        <dt>Justification</dt>
        <dd>{{ detail.justification }}</dd>
        <dt>Approval Date</dt>
        <dd>{{ detail.approvalDate }}</dd>
        <dt>Review Date</dt>
        <dd>{{ detail.reviewDate }}</dd>
        <dt>Status</dt>
        <dd>{{ detail.statusName }}</dd>
        <dt>External Ticket</dt>
        <dd>{{ detail.externalTicketReference }}</dd>
      </dl>

      <template v-if="detail.statusName === 'Active'">
        <h3>Re-approve (extend review date)</h3>
        <input v-model="newReviewDate" type="date" />
        <button :disabled="saving" @click="extendReview">Extend Review Date</button>

        <h3>Revoke</h3>
        <button :disabled="saving" @click="revoke">Revoke Exception</button>
      </template>
    </template>

    <template v-else>
      <form @submit.prevent="createException">
        <p>
          <label><input v-model="scopeType" type="radio" value="Account" /> Account</label>
          <label><input v-model="scopeType" type="radio" value="Application" /> Application</label>
        </p>
        <p v-if="scopeType === 'Account'">
          <label>Account Key: <input v-model="accountKey" type="number" required /></label>
        </p>
        <p v-else>
          <label>
            Application:
            <select v-model="applicationKey" required>
              <option value="" disabled>Select an application</option>
              <option v-for="app in applications" :key="app.applicationKey" :value="app.applicationKey">
                {{ app.applicationName }}
              </option>
            </select>
          </label>
        </p>
        <p>
          <label>Justification: <textarea v-model="justification" required></textarea></label>
        </p>
        <p>
          <label>Review Date: <input v-model="reviewDate" type="date" required /></label>
        </p>
        <p>
          <label>External Ticket Reference: <input v-model="externalTicketReference" type="text" /></label>
        </p>
        <button type="submit" :disabled="saving">Create Exception</button>
      </form>
    </template>
  </div>
</template>
