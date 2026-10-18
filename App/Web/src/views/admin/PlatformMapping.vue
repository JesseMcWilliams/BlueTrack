<script setup>
// D-247: Admin > Platform Mapping. Each CyberArk platform gets the account type and source of record its accounts get
// by default (a new account is given them when it is first tracked). Self-Hosted platforms are one per PolicyID
// (D-244), so a new import can bring new, unmapped platforms. "Fill" also gives them to accounts already tracked that
// have none; an account that has an account type or source of record is never changed.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()

const rows = ref([])
const options = ref({ accountTypes: [], sourcesOfRecord: [] })
const drafts = ref({})
const loading = ref(true)
const error = ref(null)
const message = ref(null)
const onlyUnmapped = ref(false)
const search = ref('')
const fillExisting = ref(true)
const busyKey = ref(null)

async function load() {
  error.value = null
  try {
    const [list, opts] = await Promise.all([fetch('/api/admin/platform-mapping'), fetch('/api/admin/platform-mapping/options')])
    if (!list.ok) throw new Error(t('common.requestFailed', { status: list.status }))
    if (!opts.ok) throw new Error(t('common.requestFailed', { status: opts.status }))
    rows.value = await list.json()
    options.value = await opts.json()
    drafts.value = Object.fromEntries(rows.value.map(r => [r.platformKey, { accountTypeKey: r.accountTypeKey, sorKey: r.sorKey, notes: r.notes ?? '' }]))
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

const unmappedCount = computed(() => rows.value.filter(r => r.isUnmapped).length)

const visibleRows = computed(() => {
  const text = search.value.trim().toLowerCase()
  return rows.value.filter(r =>
    (!onlyUnmapped.value || r.isUnmapped) &&
    (!text || r.platformId.toLowerCase().includes(text) || (r.platformName ?? '').toLowerCase().includes(text)))
})

function changed(row) {
  const draft = drafts.value[row.platformKey]
  return draft.accountTypeKey !== row.accountTypeKey || (draft.sorKey ?? null) !== (row.sorKey ?? null) || (draft.notes ?? '') !== (row.notes ?? '')
}

async function save(row) {
  const draft = drafts.value[row.platformKey]
  if (!draft.accountTypeKey) return
  error.value = null
  message.value = null
  busyKey.value = row.platformKey
  try {
    const response = await fetch(`/api/admin/platform-mapping/${row.platformKey}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ accountTypeKey: draft.accountTypeKey, sorKey: draft.sorKey || null, notes: draft.notes || null, fillExisting: fillExisting.value })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      error.value = problem?.error ?? t('common.saveFailed', { status: response.status })
      return
    }
    const result = await response.json()
    message.value = fillExisting.value
      ? t('admin.platformMapping.savedFilled', { platform: row.platformId, types: formatCount(result.accountsTypeFilled), sors: formatCount(result.accountsSorFilled) })
      : t('admin.platformMapping.saved', { platform: row.platformId })
    await load()
  } finally {
    busyKey.value = null
  }
}

async function remove(row) {
  if (!(await confirmDelete(t('admin.platformMapping.confirmRemove', { platform: row.platformId }), t('common.remove')))) return
  error.value = null
  message.value = null
  const response = await fetch(`/api/admin/platform-mapping/${row.platformKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  message.value = t('admin.platformMapping.removed', { platform: row.platformId })
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.platformMapping.title') }}</h2>
    <p>{{ t('admin.platformMapping.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p v-if="unmappedCount > 0">{{ t('admin.platformMapping.unmappedCount', { count: formatCount(unmappedCount) }) }}</p>
      <p class="filter-row">
        <label class="field-label"><span class="field-label-text">{{ t('admin.platformMapping.search') }}</span> <input v-model="search" type="search" /></label>
        <label><input v-model="onlyUnmapped" type="checkbox" /> {{ t('admin.platformMapping.onlyUnmapped') }}</label>
      </p>
      <p>
        <label><input v-model="fillExisting" type="checkbox" /> {{ t('admin.platformMapping.fillExisting') }}</label>
        <br /><small>{{ t('admin.platformMapping.fillHint') }}</small>
      </p>

      <p v-if="visibleRows.length === 0">{{ t('admin.platformMapping.noPlatforms') }}</p>
      <table v-else>
        <thead>
          <tr>
            <th>{{ t('admin.platformMapping.source') }}</th>
            <th>{{ t('admin.platformMapping.platform') }}</th>
            <th>{{ t('admin.platformMapping.accounts') }}</th>
            <th>{{ t('admin.platformMapping.withoutType') }}</th>
            <th>{{ t('admin.platformMapping.accountType') }}</th>
            <th>{{ t('admin.platformMapping.sourceOfRecord') }}</th>
            <th>{{ t('admin.platformMapping.notes') }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in visibleRows" :key="row.platformKey">
            <td>{{ row.sourceSystemName }}</td>
            <td>{{ row.platformId }}<template v-if="row.platformName && row.platformName !== row.platformId"> ({{ row.platformName }})</template></td>
            <td>{{ formatCount(row.accountCount) }}</td>
            <td>{{ formatCount(row.accountsWithoutType) }}</td>
            <td>
              <select v-model="drafts[row.platformKey].accountTypeKey" :aria-label="t('admin.platformMapping.accountTypeFor', { platform: row.platformId })">
                <option :value="null">{{ t('admin.platformMapping.notMapped') }}</option>
                <option v-for="type in options.accountTypes" :key="type.key" :value="type.key">{{ type.name }}</option>
              </select>
            </td>
            <td>
              <select v-model="drafts[row.platformKey].sorKey" :aria-label="t('admin.platformMapping.sorFor', { platform: row.platformId })">
                <option :value="null">{{ t('common.none') }}</option>
                <option v-for="sor in options.sourcesOfRecord" :key="sor.key" :value="sor.key">{{ sor.name }}</option>
              </select>
            </td>
            <td><input v-model="drafts[row.platformKey].notes" maxlength="500" :aria-label="t('admin.platformMapping.notesFor', { platform: row.platformId })" /></td>
            <td>
              <button type="button" :disabled="busyKey === row.platformKey || !drafts[row.platformKey].accountTypeKey || !changed(row)" @click="save(row)">{{ t('common.save') }}</button>
              <button v-if="!row.isUnmapped" type="button" @click="remove(row)">{{ t('common.remove') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>
