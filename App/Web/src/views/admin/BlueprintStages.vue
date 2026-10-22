<script setup>
// D-284: Admin > Blueprint Stages -- rename, reorder (Move up / Move down), add and delete the stages; mark where new
// accounts start and which stage each KPI milestone (Onboarded, Managed, Compliant) counts from.
// /api/admin/blueprint-stages (ManageProgressRules). A stage in use (accounts at it, a rule, its history, the start
// flag or a milestone) can't be deleted; the API says why.
import { ref, onMounted } from 'vue'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()

const stages = ref([])
const drafts = ref({})
const error = ref(null)
const notice = ref(null)
const loading = ref(true)
const busy = ref(false)
const newName = ref('')

const milestones = ['Onboarded', 'Managed', 'Compliant']
const milestoneLabel = m => ({
  Onboarded: t('admin.blueprintStages.milestoneOnboarded'),
  Managed: t('admin.blueprintStages.milestoneManaged'),
  Compliant: t('admin.blueprintStages.milestoneCompliant')
})[m] ?? m

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/blueprint-stages')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    stages.value = await response.json()
    drafts.value = Object.fromEntries(stages.value.map(s => [s.stageKey, { stageName: s.stageName, isStartStage: s.isStartStage, milestone: s.milestone ?? '' }]))
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function send(url, method, body, doneMessage) {
  error.value = null
  notice.value = null
  busy.value = true
  try {
    const response = await fetch(url, { method, headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      error.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return false
    }
    notice.value = doneMessage
    await load()
    return true
  } finally {
    busy.value = false
  }
}

function save(stage) {
  const d = drafts.value[stage.stageKey]
  return send(`/api/admin/blueprint-stages/${stage.stageKey}`, 'PUT',
    { stageName: d.stageName, isStartStage: d.isStartStage, milestone: d.milestone || null },
    t('admin.blueprintStages.saved', { name: d.stageName }))
}

function move(index, delta) {
  const keys = stages.value.map(s => s.stageKey)
  const [key] = keys.splice(index, 1)
  keys.splice(index + delta, 0, key)
  return send('/api/admin/blueprint-stages/order', 'PUT', { stageKeys: keys }, t('admin.blueprintStages.reordered'))
}

async function add() {
  if (!newName.value.trim()) return
  if (await send('/api/admin/blueprint-stages', 'POST', { stageName: newName.value.trim() }, t('admin.blueprintStages.added', { name: newName.value.trim() }))) {
    newName.value = ''
  }
}

async function remove(stage) {
  if (!(await confirmDelete(t('admin.blueprintStages.confirmDelete', { name: stage.stageName })))) return
  await send(`/api/admin/blueprint-stages/${stage.stageKey}`, 'DELETE', null, t('admin.blueprintStages.deleted', { name: stage.stageName }))
}
</script>

<template>
  <div>
    <h2>{{ t('admin.blueprintStages.title') }}</h2>
    <p class="hint">{{ t('admin.blueprintStages.intro') }}</p>
    <p><router-link :to="{ name: 'admin-progress-rules' }">{{ t('admin.blueprintStages.rulesLink') }}</router-link></p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <table>
        <thead>
          <tr>
            <th>{{ t('admin.blueprintStages.colOrder') }}</th>
            <th>{{ t('common.name') }}</th>
            <th>{{ t('admin.blueprintStages.colStart') }}</th>
            <th>{{ t('admin.blueprintStages.colMilestone') }}</th>
            <th>{{ t('admin.blueprintStages.colAccounts') }}</th>
            <th><span class="visually-hidden">{{ t('admin.blueprintStages.colActions') }}</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(s, i) in stages" :key="s.stageKey">
            <td>{{ i + 1 }}</td>
            <td><label><span class="visually-hidden">{{ t('admin.blueprintStages.nameOf', { n: i + 1 }) }}</span><input v-model="drafts[s.stageKey].stageName" maxlength="100" /></label></td>
            <td><label><span class="visually-hidden">{{ t('admin.blueprintStages.startOf', { n: i + 1 }) }}</span><input v-model="drafts[s.stageKey].isStartStage" type="checkbox" /></label></td>
            <td>
              <label><span class="visually-hidden">{{ t('admin.blueprintStages.milestoneOf', { n: i + 1 }) }}</span>
                <select v-model="drafts[s.stageKey].milestone">
                  <option value="">{{ t('common.none') }}</option>
                  <option v-for="m in milestones" :key="m" :value="m">{{ milestoneLabel(m) }}</option>
                </select>
              </label>
            </td>
            <td>{{ formatCount(s.accountCount) }}</td>
            <td class="actions">
              <button type="button" :disabled="busy" @click="save(s)">{{ t('common.save') }}</button>
              <button type="button" :disabled="busy || i === 0" :aria-label="t('admin.blueprintStages.moveUpLabel', { name: s.stageName })" @click="move(i, -1)">{{ t('admin.blueprintStages.moveUp') }}</button>
              <button type="button" :disabled="busy || i === stages.length - 1" :aria-label="t('admin.blueprintStages.moveDownLabel', { name: s.stageName })" @click="move(i, 1)">{{ t('admin.blueprintStages.moveDown') }}</button>
              <button type="button" :disabled="busy" @click="remove(s)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <form class="add" @submit.prevent="add">
        <label class="field-label"><span class="field-label-text">{{ t('admin.blueprintStages.newStage') }}</span> <input v-model="newName" maxlength="100" /></label>
        <button type="submit" :disabled="busy || !newName.trim()">{{ t('admin.blueprintStages.add') }}</button>
      </form>
    </template>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.actions {
  white-space: nowrap;
}
.add {
  margin-top: 1rem;
  display: flex;
  gap: 0.5rem;
  align-items: flex-end;
}
</style>
