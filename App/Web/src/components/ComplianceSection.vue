<script setup>
// D-300/D-301: on an account's page, its Safe's last Safe access policy check (and what failed) and the account's
// compliance review: who reviewed it and when. Mark reviewed / Clear review need ReviewAccountCompliance; a review
// lasts until the account moves to another Safe or someone clears it. GET /api/account-progress/{key}/compliance,
// PUT/DELETE .../compliance-review.
import { ref, onMounted } from 'vue'
import { formatUtcDateTime } from '../utils/formatLocale'
import { useRightsStore } from '../stores/rights'
import { useI18n } from 'vue-i18n'

const props = defineProps({ accountKey: { type: [String, Number], required: true } })
const { t } = useI18n()
const rights = useRightsStore()

const compliance = ref(null)
const error = ref(null)
const actionError = ref(null)
const note = ref('')
const busy = ref(false)

async function load() {
  try {
    const response = await fetch(`/api/account-progress/${props.accountKey}/compliance`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    compliance.value = await response.json()
  } catch (err) {
    error.value = err.message
  }
}

onMounted(load)

async function send(method) {
  actionError.value = null
  busy.value = true
  try {
    const response = await fetch(`/api/account-progress/${props.accountKey}/compliance-review`, {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: method === 'PUT' ? JSON.stringify({ note: note.value }) : undefined
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      actionError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    note.value = ''
    await load()
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="compliance" aria-labelledby="compliance-heading">
    <h2 id="compliance-heading">{{ t('pages.compliance.title') }}</h2>
    <p v-if="error" role="alert">{{ t('pages.compliance.loadFailed', { error }) }}</p>
    <p v-else-if="!compliance" role="status">{{ t('common.loading') }}</p>
    <template v-else>
      <p v-if="compliance.isCompliant === true">{{ t('pages.compliance.passed', { policy: compliance.policyName, date: formatUtcDateTime(compliance.checkedAt) }) }}</p>
      <template v-else-if="compliance.isCompliant === false">
        <p class="compliance-failed">{{ t('pages.compliance.failed', { policy: compliance.policyName, date: formatUtcDateTime(compliance.checkedAt) }) }}</p>
        <ul class="compliance-problems">
          <li v-for="(problem, i) in compliance.problems" :key="i">{{ problem }}</li>
        </ul>
      </template>
      <p v-else>{{ t('pages.compliance.noPolicy') }}</p>

      <p v-if="compliance.reviewedAt" class="compliance-reviewed">
        {{ t('pages.compliance.reviewed', { name: compliance.reviewedByName, date: formatUtcDateTime(compliance.reviewedAt) }) }}
        <template v-if="compliance.reviewNote">{{ ' ' + t('pages.compliance.reviewNote', { note: compliance.reviewNote }) }}</template>
      </p>
      <p v-else>{{ t('pages.compliance.notReviewed') }}</p>

      <div v-if="rights.hasPermission('ReviewAccountCompliance')" class="compliance-actions">
        <label class="field-label"><span class="field-label-text">{{ t('pages.compliance.noteLabel') }}</span> <input v-model="note" maxlength="1000" /></label>
        <button type="button" :disabled="busy" @click="send('PUT')">{{ compliance.reviewedAt ? t('pages.compliance.reviewAgain') : t('pages.compliance.markReviewed') }}</button>
        <button v-if="compliance.reviewedAt" type="button" :disabled="busy" @click="send('DELETE')">{{ t('pages.compliance.clearReview') }}</button>
        <p v-if="actionError" role="alert">{{ actionError }}</p>
      </div>
    </template>
  </section>
</template>

<style scoped>
.compliance-failed {
  font-weight: 600;
}
.compliance-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  align-items: center;
}
</style>
