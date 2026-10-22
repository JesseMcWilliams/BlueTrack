<script setup>
// D-283: add or edit a Progress Rule: the stage it moves accounts from and to (a later one; the next by default),
// and conditions that must all match. Test shows how many accounts at the From stage it would move now, without
// saving. POST/PUT /api/admin/progress-rules, POST .../preview, GET .../fields; the stages from
// GET /api/admin/blueprint-stages.
import { ref, computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { flash, savedMessage } from '../../composables/useFlash'
import { useI18n } from 'vue-i18n'
import { fieldLabels, operatorLabels, NO_VALUE } from './progressRuleLabels'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()
const props = defineProps({ ruleKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.ruleKey !== null && props.ruleKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const stages = ref([])
const fields = ref([])
const preview = ref(null)
const previewError = ref(null)
const previewing = ref(false)

const editing = ref({ ruleName: '', fromStageKey: null, toStageKey: null, isEnabled: true, description: '', conditions: [] })

const fieldNames = computed(() => fieldLabels(t))
const operatorNames = computed(() => operatorLabels(t))
const laterStages = computed(() => {
  const from = stages.value.find(s => s.stageKey === editing.value.fromStageKey)
  return from ? stages.value.filter(s => s.stageOrder > from.stageOrder) : []
})

function fieldOf(condition) {
  return fields.value.find(f => f.fieldName === condition.fieldName)
}

function valueHint(condition) {
  const kind = fieldOf(condition)?.kind
  if (kind === 'Date') return t('admin.progressRuleEdit.daysHint')
  if (condition.operator === 'In' || condition.operator === 'NotIn') return t('admin.progressRuleEdit.listHint')
  if (condition.fieldName === 'Source') return t('admin.progressRuleEdit.sourceHint')
  return ''
}

// Picking a From stage picks the next stage as To, unless To is already a later stage.
watch(() => editing.value.fromStageKey, () => {
  if (!laterStages.value.some(s => s.stageKey === editing.value.toStageKey)) {
    editing.value.toStageKey = laterStages.value[0]?.stageKey ?? null
  }
})

function onFieldChange(condition) {
  const ops = fieldOf(condition)?.operators ?? []
  if (!ops.includes(condition.operator)) condition.operator = ops[0] ?? ''
  if (fieldOf(condition)?.kind === 'YesNo' && !['Yes', 'No'].includes(condition.value)) condition.value = 'Yes'
}

function addCondition() {
  const condition = { fieldName: fields.value[0]?.fieldName ?? '', operator: '', value: '' }
  onFieldChange(condition)
  editing.value.conditions.push(condition)
}

function removeCondition(index) {
  editing.value.conditions.splice(index, 1)
}

onMounted(async () => {
  try {
    const [stagesResponse, fieldsResponse, rulesResponse] = await Promise.all([
      fetch('/api/admin/blueprint-stages'),
      fetch('/api/admin/progress-rules/fields'),
      isEditMode.value ? fetch('/api/admin/progress-rules') : Promise.resolve(null)
    ])
    for (const r of [stagesResponse, fieldsResponse, rulesResponse]) {
      if (r && !r.ok) throw new Error(t('common.requestFailed', { status: r.status }))
    }
    stages.value = await stagesResponse.json()
    fields.value = await fieldsResponse.json()
    if (rulesResponse) {
      const match = (await rulesResponse.json()).find(r => String(r.progressRuleKey) === String(props.ruleKey))
      if (!match) throw new Error(t('admin.progressRuleEdit.notFound'))
      editing.value = { ...match, description: match.description ?? '', conditions: match.conditions.map(c => ({ ...c, value: c.value ?? '' })) }
    } else {
      editing.value.fromStageKey = stages.value[0]?.stageKey ?? null
      addCondition()
    }
  } catch (err) {
    loadError.value = err.message
  }
  loading.value = false
})

function body() {
  return {
    ruleName: editing.value.ruleName,
    fromStageKey: editing.value.fromStageKey,
    toStageKey: editing.value.toStageKey,
    isEnabled: editing.value.isEnabled,
    description: editing.value.description,
    conditions: editing.value.conditions.map(c => ({ fieldName: c.fieldName, operator: c.operator, value: NO_VALUE.includes(c.operator) ? null : c.value }))
  }
}

async function problemText(response, fallback) {
  const problem = await response.json().catch(() => null)
  return problem?.detail ?? problem?.title ?? fallback
}

async function test() {
  previewError.value = null
  preview.value = null
  previewing.value = true
  try {
    const response = await fetch('/api/admin/progress-rules/preview', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body())
    })
    if (!response.ok) {
      previewError.value = await problemText(response, t('common.requestFailed', { status: response.status }))
      return
    }
    preview.value = await response.json()
  } finally {
    previewing.value = false
  }
}

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/progress-rules/${props.ruleKey}` : '/api/admin/progress-rules'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body())
    })
    if (!response.ok) {
      saveError.value = await problemText(response, t('common.saveFailed', { status: response.status }))
      return
    }
    flash(savedMessage(t, editing.value.ruleName))
    router.push({ name: 'admin-progress-rules' })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? t('admin.progressRuleEdit.editTitle') : t('admin.progressRuleEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.progressRuleEdit.name') }}</span> <input v-model="editing.ruleName" required maxlength="200" /></label></p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.progressRuleEdit.from') }}</span>
          <select v-model="editing.fromStageKey" required>
            <option v-for="s in stages" :key="s.stageKey" :value="s.stageKey">{{ s.stageName }}</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.progressRuleEdit.to') }}</span>
          <select v-model="editing.toStageKey" required>
            <option v-for="s in laterStages" :key="s.stageKey" :value="s.stageKey">{{ s.stageName }}</option>
          </select>
        </label>
      </p>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('admin.progressRuleEdit.enabled') }}</label></p>

      <fieldset>
        <legend>{{ t('admin.progressRuleEdit.conditions') }}</legend>
        <p class="hint">{{ t('admin.progressRuleEdit.conditionsHint') }}</p>
        <div v-for="(c, i) in editing.conditions" :key="i" class="condition">
          <label>
            <span class="visually-hidden">{{ t('admin.progressRuleEdit.field', { n: i + 1 }) }}</span>
            <select v-model="c.fieldName" @change="onFieldChange(c)">
              <option v-for="f in fields" :key="f.fieldName" :value="f.fieldName">{{ fieldNames[f.fieldName] ?? f.fieldName }}</option>
            </select>
          </label>
          <label>
            <span class="visually-hidden">{{ t('admin.progressRuleEdit.operator', { n: i + 1 }) }}</span>
            <select v-model="c.operator">
              <option v-for="op in fieldOf(c)?.operators ?? []" :key="op" :value="op">{{ operatorNames[op] ?? op }}</option>
            </select>
          </label>
          <template v-if="!NO_VALUE.includes(c.operator)">
            <label v-if="fieldOf(c)?.kind === 'YesNo'">
              <span class="visually-hidden">{{ t('admin.progressRuleEdit.value', { n: i + 1 }) }}</span>
              <select v-model="c.value"><option value="Yes">{{ t('common.yes') }}</option><option value="No">{{ t('common.no') }}</option></select>
            </label>
            <label v-else>
              <span class="visually-hidden">{{ t('admin.progressRuleEdit.value', { n: i + 1 }) }}</span>
              <input v-model="c.value" :type="fieldOf(c)?.kind === 'Date' ? 'number' : 'text'" :min="fieldOf(c)?.kind === 'Date' ? 0 : undefined" maxlength="400" required />
            </label>
            <small v-if="valueHint(c)">{{ valueHint(c) }}</small>
          </template>
          <button type="button" :aria-label="t('admin.progressRuleEdit.removeCondition', { n: i + 1 })" @click="removeCondition(i)">{{ t('common.remove') }}</button>
        </div>
        <p><button type="button" @click="addCondition">{{ t('admin.progressRuleEdit.addCondition') }}</button></p>
      </fieldset>

      <p><label class="field-label"><span class="field-label-text">{{ t('admin.progressRuleEdit.description') }}</span> <textarea v-model="editing.description" rows="2" maxlength="1000"></textarea></label></p>

      <p>
        <button type="button" :disabled="previewing" @click="test">{{ t('admin.progressRuleEdit.test') }}</button>
      </p>
      <p v-if="previewError" role="alert">{{ previewError }}</p>
      <div v-if="preview" role="status" class="preview">
        <p>{{ t('admin.progressRuleEdit.previewCount', { count: formatCount(preview.matchCount) }, preview.matchCount) }}</p>
        <ul v-if="preview.accounts.length">
          <li v-for="a in preview.accounts" :key="a.accountKey">
            <router-link :to="{ name: 'account-progress-detail', params: { accountKey: a.accountKey } }">{{ a.accountName }}</router-link>
            <small v-if="a.safeName"> ({{ a.safeName }})</small>
          </li>
        </ul>
      </div>

      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="router.push({ name: 'admin-progress-rules' })">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.condition {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  align-items: center;
  margin-bottom: 0.5rem;
}
.preview {
  max-width: 60em;
}
</style>
