<script setup>
// D-283: add or edit a Progress Rule: the stage it moves accounts from and to (a later one; the next by default),
// and conditions that must all match. D-298: or an archive rule, which sets matching accounts at any stage to
// Archived and has no stages. Test shows how many accounts it would move (or archive) now, without saving.
// POST/PUT /api/admin/progress-rules, POST .../preview, GET .../fields; the stages from GET /api/admin/blueprint-stages.
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
const sources = ref([])   // D-338: the source systems set up here, for a Source condition
const preview = ref(null)
const previewError = ref(null)
const previewing = ref(false)

const editing = ref({ ruleName: '', ruleAction: 'MoveStage', fromStageKey: null, toStageKey: null, isEnabled: true, description: '', conditions: [] })
const isArchive = computed(() => editing.value.ruleAction === 'Archive')

const fieldNames = computed(() => fieldLabels(t))
const operatorNames = computed(() => operatorLabels(t))
const laterStages = computed(() => {
  const from = stages.value.find(s => s.stageKey === editing.value.fromStageKey)
  return from ? stages.value.filter(s => s.stageOrder > from.stageOrder) : []
})

function fieldOf(condition) {
  return fields.value.find(f => f.fieldName === condition.fieldName)
}

// 2026-11-05: the field list in alphabetical order of what it shows.
const sortedFields = computed(() => [...fields.value].sort((a, b) =>
  (fieldNames.value[a.fieldName] ?? a.fieldName).localeCompare(fieldNames.value[b.fieldName] ?? b.fieldName, undefined, { sensitivity: 'base' })))

// D-338: a Source condition picks from the source systems: one for Equals / Not equals, any number for In / Not in.
// A saved value that isn't one of them (a feed's source since removed) is still listed, so it isn't lost.
function sourcePick(condition) {
  if (condition.fieldName !== 'Source') return null
  if (['Equals', 'NotEquals'].includes(condition.operator)) return 'one'
  if (['In', 'NotIn'].includes(condition.operator)) return 'many'
  return null
}

function listValues(condition) {
  return (condition.value ?? '').split(',').map(v => v.trim()).filter(Boolean)
}

function sameCode(a, b) {
  return a.localeCompare(b, undefined, { sensitivity: 'base' }) === 0
}

function sourceOptions(condition) {
  const known = sources.value.map(s => ({ code: s.sourceSystemCode, label: `${s.sourceSystemName} (${s.sourceSystemCode})` }))
  const extra = listValues(condition)
    .filter(v => !known.some(k => sameCode(k.code, v)))
    .map(v => ({ code: v, label: v }))
  return [...known, ...extra]
}

function hasSource(condition, code) {
  return listValues(condition).some(v => sameCode(v, code))
}

function toggleSource(condition, code, on) {
  const others = listValues(condition).filter(v => !sameCode(v, code))
  condition.value = (on ? [...others, code] : others).join(', ')
}

function valueHint(condition) {
  const kind = fieldOf(condition)?.kind
  if (sourcePick(condition)) return ''
  if (kind === 'Date') return t('admin.progressRuleEdit.daysHint')
  if (condition.operator === 'In' || condition.operator === 'NotIn') return t('admin.progressRuleEdit.listHint')
  if (condition.fieldName === 'Source') return t('admin.progressRuleEdit.sourceHint')
  if (condition.fieldName === 'Folder') return t('admin.progressRuleEdit.folderHint')
  if (condition.fieldName === 'CpmDisabledReason') return t('admin.progressRuleEdit.cpmDisabledReasonHint')
  return ''
}

// Picking a From stage picks the next stage as To, unless To is already a later stage.
watch(() => editing.value.fromStageKey, () => {
  if (!laterStages.value.some(s => s.stageKey === editing.value.toStageKey)) {
    editing.value.toStageKey = laterStages.value[0]?.stageKey ?? null
  }
})

// The kind of box a condition's value goes in. A field of another kind starts its value afresh: a source code or Yes
// left in a number-of-days box shows as empty but is still the value, and the save is refused.
function valueBox(condition) {
  if (fieldOf(condition)?.kind === 'YesNo') return 'YesNo'
  if (condition.fieldName === 'Source') return 'Source'
  return fieldOf(condition)?.kind === 'Date' ? 'Days' : 'Text'
}

function onFieldChange(condition, fieldName = condition.fieldName) {
  const before = valueBox(condition)
  condition.fieldName = fieldName
  if (valueBox(condition) !== before) condition.value = ''
  const ops = fieldOf(condition)?.operators ?? []
  if (!ops.includes(condition.operator)) condition.operator = ops[0] ?? ''
  if (fieldOf(condition)?.kind === 'YesNo' && !['Yes', 'No'].includes(condition.value)) condition.value = 'Yes'
  if (sourcePick(condition) === 'one' && !condition.value) condition.value = sources.value[0]?.sourceSystemCode ?? ''
}

function addCondition() {
  // A new condition starts on Source, as it always has.
  const first = fields.value.find(f => f.fieldName === 'Source') ?? sortedFields.value[0]
  const condition = { fieldName: '', operator: '', value: '' }
  onFieldChange(condition, first?.fieldName ?? '')
  editing.value.conditions.push(condition)
}

function removeCondition(index) {
  editing.value.conditions.splice(index, 1)
}

onMounted(async () => {
  try {
    const [stagesResponse, fieldsResponse, rulesResponse, sourcesResponse] = await Promise.all([
      fetch('/api/admin/blueprint-stages'),
      fetch('/api/admin/progress-rules/fields'),
      isEditMode.value ? fetch('/api/admin/progress-rules') : Promise.resolve(null),
      fetch('/api/admin/progress-rules/sources')
    ])
    for (const r of [stagesResponse, fieldsResponse, rulesResponse, sourcesResponse]) {
      if (r && !r.ok) throw new Error(t('common.requestFailed', { status: r.status }))
    }
    stages.value = await stagesResponse.json()
    fields.value = await fieldsResponse.json()
    sources.value = await sourcesResponse.json()
    if (rulesResponse) {
      const match = (await rulesResponse.json()).find(r => String(r.progressRuleKey) === String(props.ruleKey))
      if (!match) throw new Error(t('admin.progressRuleEdit.notFound'))
      editing.value = {
        ...match,
        fromStageKey: match.fromStageKey ?? stages.value[0]?.stageKey ?? null,
        description: match.description ?? '',
        conditions: match.conditions.map(c => ({ ...c, value: c.value ?? '' }))
      }
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
    ruleAction: editing.value.ruleAction,
    fromStageKey: isArchive.value ? null : editing.value.fromStageKey,
    toStageKey: isArchive.value ? null : editing.value.toStageKey,
    isEnabled: editing.value.isEnabled,
    description: editing.value.description,
    // Always text: v-model makes a number box's value a number, which the API's text Value refuses.
    conditions: editing.value.conditions.map(c => ({ fieldName: c.fieldName, operator: c.operator, value: NO_VALUE.includes(c.operator) ? null : String(c.value ?? '') }))
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
        <label class="field-label"><span class="field-label-text">{{ t('admin.progressRuleEdit.action') }}</span>
          <select v-model="editing.ruleAction">
            <option value="MoveStage">{{ t('admin.progressRuleEdit.actionMove') }}</option>
            <option value="Archive">{{ t('admin.progressRuleEdit.actionArchive') }}</option>
          </select>
        </label>
      </p>
      <template v-if="!isArchive">
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
      </template>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('admin.progressRuleEdit.enabled') }}</label></p>

      <fieldset>
        <legend>{{ t('admin.progressRuleEdit.conditions') }}</legend>
        <p class="hint">{{ t('admin.progressRuleEdit.conditionsHint') }}</p>
        <div v-for="(c, i) in editing.conditions" :key="i" class="condition">
          <label>
            <span class="visually-hidden">{{ t('admin.progressRuleEdit.field', { n: i + 1 }) }}</span>
            <select :value="c.fieldName" @change="onFieldChange(c, $event.target.value)">
              <option v-for="f in sortedFields" :key="f.fieldName" :value="f.fieldName">{{ fieldNames[f.fieldName] ?? f.fieldName }}</option>
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
            <label v-else-if="sourcePick(c) === 'one'">
              <span class="visually-hidden">{{ t('admin.progressRuleEdit.value', { n: i + 1 }) }}</span>
              <select v-model="c.value" class="source-value" required>
                <option v-for="o in sourceOptions(c)" :key="o.code" :value="o.code">{{ o.label }}</option>
              </select>
            </label>
            <fieldset v-else-if="sourcePick(c) === 'many'" class="source-values">
              <legend class="visually-hidden">{{ t('admin.progressRuleEdit.value', { n: i + 1 }) }}</legend>
              <label v-for="o in sourceOptions(c)" :key="o.code">
                <input type="checkbox" :checked="hasSource(c, o.code)" @change="toggleSource(c, o.code, $event.target.checked)" /> {{ o.label }}
              </label>
            </fieldset>
            <label v-else>
              <span class="visually-hidden">{{ t('admin.progressRuleEdit.value', { n: i + 1 }) }}</span>
              <input v-model="c.value" :type="fieldOf(c)?.kind === 'Date' ? 'number' : 'text'" :min="fieldOf(c)?.kind === 'Date' ? 0 : undefined" maxlength="400" required
                     :list="c.fieldName === 'Source' ? 'progress-rule-sources' : undefined" />
            </label>
            <small v-if="valueHint(c)">{{ valueHint(c) }}</small>
          </template>
          <button type="button" :aria-label="t('admin.progressRuleEdit.removeCondition', { n: i + 1 })" @click="removeCondition(i)">{{ t('common.remove') }}</button>
        </div>
        <p><button type="button" @click="addCondition">{{ t('admin.progressRuleEdit.addCondition') }}</button></p>
        <datalist id="progress-rule-sources">
          <option v-for="src in sources" :key="src.sourceSystemCode" :value="src.sourceSystemCode">{{ src.sourceSystemName }}</option>
        </datalist>
      </fieldset>

      <p><label class="field-label"><span class="field-label-text">{{ t('admin.progressRuleEdit.description') }}</span> <textarea v-model="editing.description" rows="2" maxlength="1000"></textarea></label></p>

      <p>
        <button type="button" :disabled="previewing" @click="test">{{ t('admin.progressRuleEdit.test') }}</button>
      </p>
      <p v-if="previewError" role="alert">{{ previewError }}</p>
      <div v-if="preview" role="status" class="preview">
        <p>{{ isArchive
          ? t('admin.progressRuleEdit.previewArchiveCount', { count: formatCount(preview.matchCount) }, preview.matchCount)
          : t('admin.progressRuleEdit.previewCount', { count: formatCount(preview.matchCount) }, preview.matchCount) }}</p>
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
