<script setup>
// D-295: the audit log's seals, on the Audit Log page. Each audit event is sealed (an HMAC over the event and the
// seal before it) within seconds of being written; Verify recomputes every seal and says where the chain breaks
// if an event or a seal was changed or deleted outside BlueTrack.
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../../utils/formatCount'
import { formatUtcDateTime } from '../../utils/formatLocale'

const { t } = useI18n()

const status = ref(null)
const result = ref(null)
const verifying = ref(false)
const error = ref(null)

// Keys are written out in full (D-227's key coverage check forbids building one at run time).
const PROBLEM_KEYS = {
  Edited: 'admin.auditChain.problemEdited',
  EventDeleted: 'admin.auditChain.problemEventDeleted',
  SealMissing: 'admin.auditChain.problemSealMissing',
  SealsMissingAtEnd: 'admin.auditChain.problemSealsMissingAtEnd'
}

function problemText(kind) {
  return PROBLEM_KEYS[kind] ? t(PROBLEM_KEYS[kind]) : kind
}

async function loadStatus() {
  try {
    const response = await fetch('/api/audit-log/chain')
    if (response.ok) status.value = await response.json()
  } catch {
    // The rest of the page works without it.
  }
}

async function verify() {
  verifying.value = true
  error.value = null
  result.value = null
  try {
    const response = await fetch('/api/audit-log/chain/verify', { method: 'POST' })
    if (!response.ok) {
      error.value = t('admin.auditChain.verifyFailed', { status: response.status })
      return
    }
    result.value = await response.json()
    await loadStatus()
  } finally {
    verifying.value = false
  }
}

onMounted(loadStatus)
</script>

<template>
  <section class="audit-chain" :aria-label="t('admin.auditChain.heading')">
    <h3>{{ t('admin.auditChain.heading') }}</h3>
    <p v-if="status">
      {{ t('admin.auditChain.sealed', { count: formatCount(status.lastChainSeq) }, status.lastChainSeq) }}
      <template v-if="status.lastSealedAt">{{ ' ' + t('admin.auditChain.lastSealed', { time: formatUtcDateTime(status.lastSealedAt) }) }}</template>
      <template v-if="status.unsealed > 0">{{ ' ' + t('admin.auditChain.waiting', { count: formatCount(status.unsealed) }) }}</template>
      <template v-if="status.lastVerifiedAt">
        <br />{{ t('admin.auditChain.lastCheck', { time: formatUtcDateTime(status.lastVerifiedAt) }) }}
        <strong :class="{ 'status-bad': status.lastVerifyIntact === false }">{{ status.lastVerifyIntact ? t('admin.auditChain.intact') : t('admin.auditChain.broken') }}</strong>
      </template>
    </p>
    <p>
      <button type="button" :disabled="verifying" @click="verify">{{ verifying ? t('admin.auditChain.verifying') : t('admin.auditChain.verify') }}</button>
      <small>{{ ' ' + t('admin.auditChain.verifyHint') }}</small>
    </p>
    <p v-if="error" role="alert">{{ error }}</p>
    <template v-if="result">
      <p v-if="result.intact" role="status">{{ t('admin.auditChain.resultIntact', { count: formatCount(result.checked) }, result.checked) }}</p>
      <template v-else>
        <p role="alert" class="status-bad">{{ t('admin.auditChain.resultBroken', { count: formatCount(result.checked) }) }}</p>
        <table>
          <thead><tr><th>{{ t('admin.auditChain.seal') }}</th><th>{{ t('admin.auditChain.event') }}</th><th>{{ t('admin.auditChain.problem') }}</th></tr></thead>
          <tbody>
            <tr v-for="(p, i) in result.problems" :key="i">
              <td>{{ p.chainSeq ?? '' }}</td>
              <td>{{ p.auditEventKey ?? '' }}</td>
              <td>{{ problemText(p.kind) }}</td>
            </tr>
          </tbody>
        </table>
        <p v-if="result.moreProblems">{{ t('admin.auditChain.moreProblems') }}</p>
        <p>{{ t('admin.auditChain.brokenAdvice') }}</p>
      </template>
    </template>
  </section>
</template>
