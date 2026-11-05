<script setup>
// D-342: DNA tracking rules on Admin > Data Sources. After each DNA import, each enabled rule tracks the DNA accounts
// that match all its conditions (each becomes a BlueTrack account at the first stage). Conditions work as the
// Progress Rules' do and ignore case. "Apply now" runs them on the accounts already imported.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { problemMessage } from '../../utils/problemText'
import { formatCount } from '../../utils/formatCount'
import { byLabel } from '../../utils/sortOptions'
import { operatorLabels, NO_VALUE } from './progressRuleLabels'
import { dnaFieldLabels } from './dnaLabels'

const { t } = useI18n()

const rules = ref([])
const fields = ref([])
const editing = ref(null)   // { key, ruleName, isEnabled, description, conditions }
const error = ref(null)
const notice = ref(null)
const busy = ref(false)

const fieldNames = computed(() => dnaFieldLabels(t))
const operatorNames = computed(() => operatorLabels(t))
const sortedFields = computed(() => byLabel(fields.value, f => fieldNames.value[f.fieldName] ?? f.fieldName))

function fieldOf(condition) {
  return fields.value.find(f => f.fieldName === condition.fieldName)
}

function describe(condition) {
  const op = operatorNames.value[condition.operator] ?? condition.operator
  const value = NO_VALUE.includes(condition.operator) ? '' : ` ${condition.value ?? ''}`
  return `${fieldNames.value[condition.fieldName] ?? condition.fieldName} ${op}${value}`
}

async function load() {
  const response = await fetch('/api/admin/dna/track-rules')
  if (!response.ok) {
    error.value = t('common.requestFailed', { status: response.status })
    return
  }
  const body = await response.json()
  fields.value = body.fields ?? []
  rules.value = body.rules ?? []
}

function onFieldChange(condition) {
  const ops = fieldOf(condition)?.operators ?? []
  if (!ops.includes(condition.operator)) condition.operator = ops[0] ?? ''
  if (fieldOf(condition)?.kind === 'YesNo' && !['Yes', 'No'].includes(condition.value)) condition.value = 'Yes'
}

function addCondition() {
  const condition = { fieldName: 'Finding', operator: '', value: '' }
  onFieldChange(condition)
  editing.value.conditions.push(condition)
}

function startNew() {
  editing.value = { key: null, ruleName: '', isEnabled: true, description: '', conditions: [] }
  addCondition()
}

function startEdit(rule) {
  editing.value = {
    key: rule.dnaTrackRuleKey, ruleName: rule.ruleName, isEnabled: rule.isEnabled, description: rule.description ?? '',
    conditions: rule.conditions.map(c => ({ ...c, value: c.value ?? '' }))
  }
}

async function save() {
  busy.value = true
  error.value = null
  notice.value = null
  try {
    const { key, ...body } = editing.value
    const response = await fetch(key ? `/api/admin/dna/track-rules/${key}` : '/api/admin/dna/track-rules', {
      method: key ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) {
      error.value = await problemMessage(response, t('common.requestFailed', { status: response.status }))
      return
    }
    editing.value = null
    notice.value = t('admin.dna.ruleSaved')
    await load()
  } finally {
    busy.value = false
  }
}

async function remove(rule) {
  if (!(await confirmDelete(t('admin.dna.confirmDeleteRule', { name: rule.ruleName })))) return
  const response = await fetch(`/api/admin/dna/track-rules/${rule.dnaTrackRuleKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.requestFailed', { status: response.status })
    return
  }
  await load()
}

async function applyNow() {
  busy.value = true
  error.value = null
  notice.value = null
  try {
    const response = await fetch('/api/admin/dna/track-rules/apply', { method: 'POST' })
    if (!response.ok) {
      error.value = t('common.requestFailed', { status: response.status })
      return
    }
    const { tracked } = await response.json()
    notice.value = t('admin.dna.rulesApplied', { count: formatCount(tracked) }, tracked)
    await load()
  } finally {
    busy.value = false
  }
}

onMounted(load)
</script>

<template>
  <div class="dna-track-rules">
    <h4>{{ t('admin.dna.rulesHeading') }}</h4>
    <p class="hint">{{ t('admin.dna.rulesIntro') }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="rules.length === 0 && !editing">{{ t('admin.dna.noRules') }}</p>
    <table v-else-if="rules.length">
      <thead>
        <tr><th>{{ t('admin.dna.ruleName') }}</th><th>{{ t('admin.dna.ruleConditions') }}</th><th>{{ t('admin.dna.ruleMatching') }}</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="rule in rules" :key="rule.dnaTrackRuleKey">
          <td>{{ rule.ruleName }}<template v-if="!rule.isEnabled"><br /><small>{{ t('admin.dna.ruleOff') }}</small></template></td>
          <td><ul class="conditions"><li v-for="(c, i) in rule.conditions" :key="i">{{ describe(c) }}</li></ul></td>
          <td>{{ formatCount(rule.matchingNow) }}</td>
          <td>
            <button type="button" @click="startEdit(rule)">{{ t('common.edit') }}</button>
            <button type="button" @click="remove(rule)">{{ t('common.delete') }}</button>
          </td>
        </tr>
      </tbody>
    </table>

    <form v-if="editing" class="dna-rule-form" @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.dna.ruleName') }}</span>
        <input v-model="editing.ruleName" maxlength="200" required /></label></p>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('admin.dna.ruleEnabled') }}</label></p>
      <fieldset>
        <legend>{{ t('admin.dna.ruleConditions') }}</legend>
        <div v-for="(c, i) in editing.conditions" :key="i" class="condition">
          <label>
            <span class="visually-hidden">{{ t('admin.dna.conditionField', { n: i + 1 }) }}</span>
            <select v-model="c.fieldName" @change="onFieldChange(c)">
              <option v-for="f in sortedFields" :key="f.fieldName" :value="f.fieldName">{{ fieldNames[f.fieldName] ?? f.fieldName }}</option>
            </select>
          </label>
          <label>
            <span class="visually-hidden">{{ t('admin.dna.conditionOperator', { n: i + 1 }) }}</span>
            <select v-model="c.operator">
              <option v-for="op in fieldOf(c)?.operators ?? []" :key="op" :value="op">{{ operatorNames[op] ?? op }}</option>
            </select>
          </label>
          <template v-if="!NO_VALUE.includes(c.operator)">
            <label v-if="fieldOf(c)?.kind === 'YesNo'">
              <span class="visually-hidden">{{ t('admin.dna.conditionValue', { n: i + 1 }) }}</span>
              <select v-model="c.value"><option value="Yes">{{ t('common.yes') }}</option><option value="No">{{ t('common.no') }}</option></select>
            </label>
            <label v-else>
              <span class="visually-hidden">{{ t('admin.dna.conditionValue', { n: i + 1 }) }}</span>
              <input v-model="c.value" :type="fieldOf(c)?.kind === 'Date' ? 'number' : 'text'" maxlength="400" required />
            </label>
          </template>
          <button type="button" @click="editing.conditions.splice(i, 1)">{{ t('common.remove') }}</button>
        </div>
        <p><button type="button" @click="addCondition">{{ t('admin.dna.addCondition') }}</button></p>
      </fieldset>
      <p>
        <button type="submit" class="btn-primary" :disabled="busy">{{ t('common.save') }}</button>
        <button type="button" @click="editing = null">{{ t('common.cancel') }}</button>
      </p>
    </form>
    <p v-else>
      <button type="button" @click="startNew">{{ t('admin.dna.newRule') }}</button>
      <button v-if="rules.length" type="button" :disabled="busy" @click="applyNow">{{ t('admin.dna.applyRules') }}</button>
    </p>
  </div>
</template>

<style scoped>
.conditions {
  margin: 0;
  padding-left: 1.1rem;
}
.condition {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  align-items: center;
  margin-bottom: 0.5rem;
}
</style>
