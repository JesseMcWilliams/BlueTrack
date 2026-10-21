<script setup>
// D-271: the shadow comparison on Data Sources. While the EVD is still the Self-Hosted source, a shadow pull reads PVWA
// each night and the SQL compares it with what the EVD import staged. This shows how many clean nights in a row there
// have been (the cutover needs ShadowCleanNights.Needed), the latest night's differences, and lets an admin mark a
// difference as expected (Internal, or Expected with a note) for the item, its whole Safe, or (for a Safe member)
// every membership of that user or group. Only unmarked "missing from Direct" and account field differences stop a
// night counting as clean; the other kinds are shown for information.
import { ref, computed, watch, onMounted } from 'vue'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatRunTime } from './dataFeedFormat'
import { useI18n } from 'vue-i18n'

const props = defineProps({
  // Bumped by the page when a shadow pull finishes, so the comparison is read again.
  refreshKey: { type: Number, default: 0 }
})

const { t } = useI18n()

const data = ref(null)
const error = ref(null)
const message = ref(null)
const category = ref('')
const blockingOnly = ref(false)
const marking = ref(null)   // { difference, scope, category, note }
const savingMark = ref(false)

const categoryLabelKeys = {
  MissingFromDirect: 'admin.shadowComparison.categoryMissing',
  OnlyInDirect: 'admin.shadowComparison.categoryOnlyInDirect',
  AccountField: 'admin.shadowComparison.categoryAccountField',
  Permission: 'admin.shadowComparison.categoryPermission',
  LogonDomain: 'admin.shadowComparison.categoryLogonDomain'
}
const kindLabelKeys = {
  Safe: 'admin.shadowComparison.kindSafe',
  Account: 'admin.shadowComparison.kindAccount',
  User: 'admin.shadowComparison.kindUser',
  Group: 'admin.shadowComparison.kindGroup',
  Member: 'admin.shadowComparison.kindMember'
}
const markCategoryLabelKeys = {
  Internal: 'admin.shadowComparison.markInternal',
  Expected: 'admin.shadowComparison.markExpected'
}
function label(keys, value) {
  const key = keys[value]
  return key ? t(key) : value
}
// D-272: a mark made by the comparison's own rules says so.
function markLabel(row) {
  const text = label(markCategoryLabelKeys, row.markCategory)
  return row.markAuto ? t('admin.shadowComparison.markAutomatic', { mark: text }) : text
}

const latest = computed(() => data.value?.runs?.[0] ?? null)

async function load() {
  error.value = null
  const query = new URLSearchParams()
  if (category.value) query.set('category', category.value)
  if (blockingOnly.value) query.set('blockingOnly', 'true')
  const response = await fetch('/api/admin/cyberark-sources/direct/comparison' + (query.size ? `?${query}` : ''))
  if (!response.ok) {
    error.value = t('common.requestFailed', { status: response.status })
    return
  }
  data.value = await response.json()
}

onMounted(load)
watch([category, blockingOnly, () => props.refreshKey], load)

function startMark(difference) {
  message.value = null
  marking.value = { difference, scope: 'Item', category: 'Expected', note: '' }
}

// A row of the Safe list: marks the whole Safe, through one of its differences.
function startSafeMark(safe) {
  message.value = null
  marking.value = {
    difference: { differenceKey: safe.anchorDifferenceKey, itemKind: 'Safe', itemName: safe.safeName, safeName: safe.safeName, safeNumber: safe.safeNumber, principalId: null },
    scope: 'Safe', category: 'Expected', note: '', safeOnly: true
  }
}

async function saveMark() {
  savingMark.value = true
  error.value = null
  try {
    const m = marking.value
    const response = await fetch('/api/admin/cyberark-sources/direct/comparison/marks', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ differenceKey: m.difference.differenceKey, scope: m.scope, category: m.category, note: m.note || null })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      error.value = problem?.detail ?? problem?.title ?? t('common.saveFailed', { status: response.status })
      return
    }
    marking.value = null
    message.value = t('admin.shadowComparison.marked')
    await load()
  } finally {
    savingMark.value = false
  }
}

async function removeMark(mark) {
  if (!(await confirmDelete(t('admin.shadowComparison.confirmRemoveMark', { name: mark.itemName ?? mark.itemKey }), t('admin.shadowComparison.removeMark')))) return
  const response = await fetch(`/api/admin/cyberark-sources/direct/comparison/marks/${mark.markKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  message.value = t('admin.shadowComparison.markRemoved')
  await load()
}
</script>

<template>
  <div class="shadow-comparison">
    <h4>{{ t('admin.shadowComparison.heading') }}</h4>
    <p>{{ t('admin.shadowComparison.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>

    <template v-if="data">
      <p class="clean-nights" :class="{ ready: data.cleanNights >= data.cleanNightsNeeded }">
        <strong>{{ t('admin.shadowComparison.cleanNights', { count: data.cleanNights, needed: data.cleanNightsNeeded }) }}</strong>
        <template v-if="data.cleanNights >= data.cleanNightsNeeded">{{ ' ' + t('admin.shadowComparison.readyToSwitch') }}</template>
      </p>

      <p v-if="!latest">{{ t('admin.shadowComparison.noRuns') }}</p>
      <template v-else>
        <p>
          {{ t('admin.shadowComparison.latestRun', { time: formatRunTime(latest.comparedAt), evdTime: formatRunTime(latest.evdStagedAt) }) }}
          <strong :class="latest.isClean ? 'outcome-Succeeded' : 'outcome-Failed'">{{ latest.isClean ? t('admin.shadowComparison.clean') : t('admin.shadowComparison.notClean', { count: latest.blockingCount }) }}</strong>
        </p>
        <p v-if="!latest.evdUsable" role="alert">{{ latest.evdNote }}</p>
        <table class="counts">
          <tbody>
            <tr><th scope="row">{{ t('admin.shadowComparison.categoryMissing') }}</th><td>{{ t('admin.shadowComparison.countExplained', { count: latest.missingFromDirect, explained: latest.missingExplained }) }}</td></tr>
            <tr><th scope="row">{{ t('admin.shadowComparison.categoryAccountField') }}</th><td>{{ t('admin.shadowComparison.countExplained', { count: latest.accountFieldDiffs, explained: latest.accountFieldExplained }) }}</td></tr>
            <tr><th scope="row">{{ t('admin.shadowComparison.categoryOnlyInDirect') }}</th><td>{{ latest.onlyInDirect }} <small>{{ t('admin.shadowComparison.informationOnly') }}</small></td></tr>
            <tr><th scope="row">{{ t('admin.shadowComparison.categoryPermission') }}</th><td>{{ latest.permissionDiffs }} <small>{{ t('admin.shadowComparison.informationOnly') }}</small></td></tr>
            <tr><th scope="row">{{ t('admin.shadowComparison.categoryLogonDomain') }}</th><td>{{ latest.logonDomainDiffs }} <small>{{ t('admin.shadowComparison.informationOnly') }}</small></td></tr>
          </tbody>
        </table>

        <h5>{{ t('admin.shadowComparison.safesHeading') }}</h5>
        <p>{{ t('admin.shadowComparison.safesIntro') }}</p>
        <p v-if="!data.safes?.length">{{ t('admin.shadowComparison.noSafes') }}</p>
        <table v-else class="safes">
          <thead>
            <tr>
              <th>{{ t('admin.shadowComparison.colSafe') }}</th><th>{{ t('admin.shadowComparison.colSeen') }}</th>
              <th>{{ t('admin.shadowComparison.colAccountsMissing') }}</th><th>{{ t('admin.shadowComparison.colMembersMissing') }}</th>
              <th>{{ t('admin.shadowComparison.colMark') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="s in data.safes" :key="s.safeNumber" :class="{ blocking: s.blocking }">
              <td>{{ s.safeName }} <small>({{ s.safeNumber }})</small></td>
              <td>{{ s.notReturned ? t('admin.shadowComparison.safeNotReturned') : t('admin.shadowComparison.safePartly') }}</td>
              <td>{{ s.accountsMissing }}</td>
              <td>{{ s.membersMissing }}</td>
              <td>
                <template v-if="!s.blocking">{{ s.markCategory ? markLabel(s) : t('admin.shadowComparison.explained') }}</template>
                <button v-else type="button" :aria-label="t('admin.shadowComparison.markSafeLabel', { name: s.safeName })" @click="startSafeMark(s)">{{ t('admin.shadowComparison.markSafe') }}</button>
              </td>
            </tr>
          </tbody>
        </table>

        <details>
          <summary>{{ t('admin.shadowComparison.history') }}</summary>
          <ul>
            <li v-for="r in data.runs" :key="r.runKey">
              {{ formatRunTime(r.comparedAt) }}:
              <span :class="r.isClean ? 'outcome-Succeeded' : 'outcome-Failed'">{{ r.isClean ? t('admin.shadowComparison.clean') : t('admin.shadowComparison.notClean', { count: r.blockingCount }) }}</span>
            </li>
          </ul>
        </details>

        <h5>{{ t('admin.shadowComparison.differencesHeading') }}</h5>
        <p class="filters">
          <label>{{ t('admin.shadowComparison.categoryFilter') }}
            <select v-model="category">
              <option value="">{{ t('admin.shadowComparison.allCategories') }}</option>
              <option v-for="(key, value) in categoryLabelKeys" :key="value" :value="value">{{ t(key) }}</option>
            </select>
          </label>
          <label><input v-model="blockingOnly" type="checkbox" /> {{ t('admin.shadowComparison.blockingOnly') }}</label>
        </p>
        <p v-if="data.differences.length === 0">{{ t('admin.shadowComparison.noDifferences') }}</p>
        <template v-else>
          <p v-if="data.totalDifferences > data.differences.length"><small>{{ t('admin.shadowComparison.showingFirst', { shown: data.differences.length, total: data.totalDifferences }) }}</small></p>
          <table>
            <thead>
              <tr>
                <th>{{ t('admin.shadowComparison.colDifference') }}</th><th>{{ t('admin.shadowComparison.colItem') }}</th><th>{{ t('admin.shadowComparison.colSafe') }}</th>
                <th>{{ t('admin.shadowComparison.colField') }}</th><th>{{ t('admin.shadowComparison.colEvd') }}</th><th>{{ t('admin.shadowComparison.colDirect') }}</th>
                <th>{{ t('admin.shadowComparison.colMark') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="d in data.differences" :key="d.differenceKey" :class="{ blocking: d.blocking }">
                <td>{{ label(categoryLabelKeys, d.category) }}</td>
                <td>{{ label(kindLabelKeys, d.itemKind) }}: {{ d.itemName ?? d.itemKey }} <small>({{ d.itemKey }})</small></td>
                <td>{{ d.safeName }}</td>
                <td>{{ d.field }}</td>
                <td>{{ d.evdValue }}</td>
                <td>{{ d.directValue }}</td>
                <td>
                  <template v-if="d.markCategory">{{ markLabel(d) }}</template>
                  <button v-else-if="d.category === 'MissingFromDirect' || d.category === 'AccountField'" type="button"
                          :aria-label="t('admin.shadowComparison.markLabel', { name: d.itemName ?? d.itemKey })" @click="startMark(d)">{{ t('admin.shadowComparison.mark') }}</button>
                </td>
              </tr>
            </tbody>
          </table>
        </template>

        <form v-if="marking" class="mark-form" @submit.prevent="saveMark">
          <fieldset>
            <legend>{{ t('admin.shadowComparison.markHeading', { name: marking.difference.itemName ?? marking.difference.itemKey }) }}</legend>
            <p>
              <label v-if="!marking.safeOnly"><input v-model="marking.scope" type="radio" value="Item" /> {{ t('admin.shadowComparison.scopeItem') }}</label>
              <label v-if="marking.difference.safeNumber !== null"><input v-model="marking.scope" type="radio" value="Safe" /> {{ t('admin.shadowComparison.scopeSafe', { safe: marking.difference.safeName }) }}</label>
              <label v-if="marking.difference.itemKind === 'Member' && marking.difference.principalId !== null"><input v-model="marking.scope" type="radio" value="Principal" /> {{ t('admin.shadowComparison.scopePrincipal', { name: marking.difference.itemName }) }}</label>
            </p>
            <p>
              <label class="field-label"><span class="field-label-text">{{ t('admin.shadowComparison.markCategoryLabel') }}</span>
                <select v-model="marking.category">
                  <option value="Internal">{{ t('admin.shadowComparison.markInternal') }}</option>
                  <option value="Expected">{{ t('admin.shadowComparison.markExpected') }}</option>
                </select>
              </label>
              <small>{{ t('admin.shadowComparison.markCategoryHint') }}</small>
            </p>
            <p>
              <label class="field-label"><span class="field-label-text">{{ t('admin.shadowComparison.noteLabel') }}</span> <input v-model="marking.note" maxlength="500" /></label>
            </p>
            <button type="submit" class="btn-primary" :disabled="savingMark">{{ t('admin.shadowComparison.saveMark') }}</button>
            <button type="button" @click="marking = null">{{ t('common.cancel') }}</button>
          </fieldset>
        </form>
      </template>

      <h5>{{ t('admin.shadowComparison.marksHeading') }}</h5>
      <p v-if="data.marks.length === 0">{{ t('admin.shadowComparison.noMarks') }}</p>
      <table v-else>
        <thead>
          <tr><th>{{ t('admin.shadowComparison.colItem') }}</th><th>{{ t('admin.shadowComparison.colMark') }}</th><th>{{ t('admin.shadowComparison.noteLabel') }}</th><th>{{ t('admin.shadowComparison.colMarkedBy') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="m in data.marks" :key="m.markKey">
            <td>{{ label(kindLabelKeys, m.itemKind) }}: {{ m.itemName ?? m.itemKey }} <small>({{ m.itemKey }})</small></td>
            <td>{{ label(markCategoryLabelKeys, m.category) }}</td>
            <td>{{ m.note }}</td>
            <td>{{ m.markedByName }} <small>{{ formatRunTime(m.markedAt) }}</small></td>
            <td><button type="button" :aria-label="t('admin.shadowComparison.removeMarkLabel', { name: m.itemName ?? m.itemKey })" @click="removeMark(m)">{{ t('admin.shadowComparison.removeMark') }}</button></td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.outcome-Failed, tr.blocking td:first-child { color: var(--color-error, #b00020); }
.clean-nights.ready { color: var(--color-success, #1b5e20); }
.filters label { margin-right: 1em; }
</style>
