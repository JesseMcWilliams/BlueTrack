<script setup>
// D-248: the detail of one Self-Hosted Direct pull, opened from Data Sources. The pull log's Message column
// holds the whole story (why a pull failed or was rejected, and any warnings, separated by " | "); the table
// shows it in small print, this shows it in full with what the outcome means and what to do about it.
// A local modal in the style of ConfirmDialog (role="dialog", Escape and the backdrop close it).
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'

const props = defineProps({ pull: { type: Object, required: true } })
const emit = defineEmits(['close'])

const { t } = useI18n()
const closeButton = ref(null)

onMounted(() => closeButton.value?.focus())

const messages = computed(() => (props.pull.message ?? '').split(' | ').map(m => m.trim()).filter(Boolean))

// Run times come from the API as UTC without a zone suffix (see dataFeedFormat.js).
function toDate(value) {
  if (!value) return null
  const date = new Date(/[zZ]|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`)
  return Number.isNaN(date.getTime()) ? null : date
}

const seconds = computed(() => {
  const start = toDate(props.pull.startedAt)
  const end = toDate(props.pull.completedAt)
  return start && end ? Math.max(0, Math.round((end - start) / 1000)) : null
})

// Keys are written out in full (D-227's key coverage check forbids building one at run time).
// D-250: Safes and accounts also show what the last good pull had, which is what the change guard compared with.
function withBaseline(value, baseline) {
  return baseline === null || baseline === undefined || baseline === value ? formatCount(value) : `${formatCount(value)} ${t('admin.dataSources.pullWas', { n: formatCount(baseline) })}`
}
const counts = computed(() => props.pull.safeCount === null || props.pull.safeCount === undefined ? [] : [
  [t('admin.dataSources.pullCount_safes'), withBaseline(props.pull.safeCount, props.pull.baselineSafeCount)],
  [t('admin.dataSources.pullCount_accounts'), withBaseline(props.pull.accountCount, props.pull.baselineAccountCount)],
  [t('admin.dataSources.pullCount_members'), formatCount(props.pull.memberCount)],
  [t('admin.dataSources.pullCount_users'), formatCount(props.pull.userCount)],
  [t('admin.dataSources.pullCount_groups'), formatCount(props.pull.groupCount)],
  [t('admin.dataSources.pullCount_platforms'), formatCount(props.pull.platformCount)]
])

const meaning = computed(() => ({
  Succeeded: t('admin.dataSources.pullMeaningSucceeded'),
  Rejected: t('admin.dataSources.pullMeaningRejected'),
  Started: t('admin.dataSources.pullMeaningStarted')
})[props.pull.status] ?? t('admin.dataSources.pullMeaningFailed'))

const advice = computed(() => props.pull.status === 'Rejected'
  ? t('admin.dataSources.pullAdviceRejected')
  : props.pull.status === 'Failed' ? t('admin.dataSources.pullAdviceFailed') : null)

function onKeydown(event) {
  if (event.key === 'Escape') emit('close')
  // Nothing else in the dialog is focusable but the Close button, so keep focus on it.
  if (event.key === 'Tab') {
    event.preventDefault()
    closeButton.value?.focus()
  }
}
</script>

<template>
  <div class="pull-details-backdrop" @click.self="emit('close')">
    <div class="pull-details" role="dialog" aria-modal="true" aria-labelledby="pull-details-title" @keydown="onKeydown">
      <h3 id="pull-details-title">{{ t('admin.dataSources.pullDetailsTitle') }}</h3>

      <p>
        <strong :class="`outcome-${pull.status}`">{{ pull.status }}</strong>
        {{ ' ' + t('admin.dataSources.pullDetailsStarted', { when: formatRunTime(pull.startedAt), by: pull.triggeredBy }) }}
        <template v-if="seconds !== null">{{ ' ' + t('admin.dataSources.pullDetailsDuration', { seconds: formatCount(seconds) }) }}</template>
        <template v-if="pull.acceptedChange">{{ ' ' + t('admin.dataSources.pullDetailsAccepted') }}</template>
      </p>
      <p>{{ meaning }}</p>

      <template v-if="messages.length">
        <h4>{{ t('admin.dataSources.pullDetailsMessages') }}</h4>
        <ul class="pull-details-messages">
          <li v-for="(m, i) in messages" :key="i">{{ m }}</li>
        </ul>
      </template>
      <p v-else>{{ t('admin.dataSources.pullDetailsNoMessages') }}</p>

      <template v-if="counts.length">
        <h4>{{ t('admin.dataSources.pullDetailsRead') }}</h4>
        <table class="pull-details-counts">
          <tbody>
            <tr v-for="[label, value] in counts" :key="label">
              <th scope="row">{{ label }}</th>
              <td>{{ value ?? '' }}</td>
            </tr>
          </tbody>
        </table>
      </template>

      <p v-if="advice"><small>{{ advice }}</small></p>

      <p class="pull-details-actions">
        <button type="button" ref="closeButton" class="btn-primary" @click="emit('close')">{{ t('common.close') }}</button>
      </p>
    </div>
  </div>
</template>

<style scoped>
.pull-details-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}
.pull-details {
  background: var(--color-background, #fff);
  color: var(--color-text, #000);
  max-width: 40rem;
  width: calc(100% - 2rem);
  max-height: 85vh;
  overflow: auto;
  padding: 1rem 1.25rem;
  border-radius: 6px;
}
.pull-details-messages { padding-left: 1.25rem; }
.pull-details-counts th { text-align: left; padding-right: 1.5rem; font-weight: normal; }
.pull-details-actions { text-align: right; }
.outcome-Failed, .outcome-Rejected { color: var(--color-error, #b00020); }
</style>
