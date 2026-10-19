<script setup>
// D-233: Admin > Account Classification (ManageDiscoveredAccounts). The
// settings and rules that give each discovered account a class (Primary /
// Secondary / Admin / Unknown) and an owner on Reports > Discovered
// Accounts. Attribute rules run first, then Name, then OU, each in the order
// shown; the first enabled match wins. Every save reclassifies on the server
// (no directory access needed). Add/Edit is its own page
// (AccountClassificationRuleEdit.vue), per D-124.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../../stores/rights'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()
const router = useRouter()
const rights = useRightsStore()

const loading = ref(true)
const error = ref(null)
const message = ref(null)
const busy = ref(false)
const settings = ref({ isEnabled: false, defaultAccountClass: 'Unknown', ownerByEmployeeId: false, employeeIdAttribute: 'employeeID' })
const rules = ref([])

const SECTIONS = [
  { type: 'Attribute', headingKey: 'admin.accountClassification.attributeRules', introKey: 'admin.accountClassification.attributeRulesIntro', newKey: 'admin.accountClassification.newAttributeRule' },
  { type: 'Name', headingKey: 'admin.accountClassification.nameRules', introKey: 'admin.accountClassification.nameRulesIntro', newKey: 'admin.accountClassification.newNameRule' },
  { type: 'OU', headingKey: 'admin.accountClassification.ouRules', introKey: 'admin.accountClassification.ouRulesIntro', newKey: 'admin.accountClassification.newOuRule' }
]

const OPERATOR_KEYS = {
  Equals: 'admin.accountClassification.opEquals',
  Contains: 'admin.accountClassification.opContains',
  StartsWith: 'admin.accountClassification.opStartsWith',
  EndsWith: 'admin.accountClassification.opEndsWith',
  IsSet: 'admin.accountClassification.opIsSet',
  Pattern: 'admin.accountClassification.opPattern'
}

const CLASS_KEYS = {
  Primary: 'admin.accountClassification.classPrimary',
  Secondary: 'admin.accountClassification.classSecondary',
  Admin: 'admin.accountClassification.classAdmin',
  Unknown: 'admin.accountClassification.classUnknown'
}

function rulesOf(type) {
  return rules.value.filter(r => r.ruleType === type)
}

function condition(rule) {
  const subject = rule.ruleType === 'OU' ? t('admin.accountClassification.distinguishedName') : rule.attributeName
  if (rule.operator === 'IsSet') return t('admin.accountClassification.conditionIsSet', { subject })
  return t('admin.accountClassification.condition', { subject, operator: t(OPERATOR_KEYS[rule.operator]), value: rule.value })
}

async function problemText(response) {
  const problem = await response.json().catch(() => null)
  return problem?.detail ?? t('common.requestFailed', { status: response.status })
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const response = await fetch('/api/admin/account-classification')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    const body = await response.json()
    settings.value = { ...body.settings }
    rules.value = body.rules
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

async function run(action, successText) {
  busy.value = true
  error.value = null
  message.value = null
  try {
    const response = await action()
    if (!response.ok) {
      error.value = await problemText(response)
      return
    }
    const body = response.status === 204 ? null : await response.json().catch(() => null)
    if (successText) message.value = successText(body)
    await load()
  } finally {
    busy.value = false
  }
}

function saveSettings() {
  return run(() => fetch('/api/admin/account-classification/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings.value)
  }), body => t('admin.accountClassification.settingsSaved', { count: formatCount(body?.reclassified ?? 0) }))
}

function move(rule, direction) {
  return run(() => fetch(`/api/admin/account-classification/rules/${rule.accountClassificationRuleKey}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ direction })
  }))
}

async function remove(rule) {
  if (!(await confirmDelete(t('admin.accountClassification.confirmDelete', { rule: condition(rule) })))) return
  return run(() => fetch(`/api/admin/account-classification/rules/${rule.accountClassificationRuleKey}`, { method: 'DELETE' }))
}

function reclassify() {
  return run(() => fetch('/api/admin/account-classification/reclassify', { method: 'POST' }),
    body => t('admin.accountClassification.reclassified', { count: formatCount(body?.reclassified ?? 0) }))
}

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('admin.accountClassification.title') }}</h2>
    <p class="intro">{{ t('admin.accountClassification.intro') }}</p>
    <!-- UI review W5-7: where the rules' effect shows. -->
    <p v-if="rights.hasPermission('ViewDiscoveredAccounts')"><router-link :to="{ name: 'reports-discovered-accounts' }">{{ t('admin.accountClassification.seeDiscovered') }}</router-link></p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <form @submit.prevent="saveSettings">
        <fieldset>
          <legend>{{ t('admin.accountClassification.settings') }}</legend>
          <p><label><input v-model="settings.isEnabled" type="checkbox" /> {{ t('admin.accountClassification.enabled') }}</label></p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassification.defaultClass') }}</span>
              <select v-model="settings.defaultAccountClass">
                <option value="Unknown">{{ t('admin.accountClassification.classUnknown') }}</option>
                <option value="Primary">{{ t('admin.accountClassification.classPrimary') }}</option>
              </select>
            </label>
          </p>
          <p><label><input v-model="settings.ownerByEmployeeId" type="checkbox" /> {{ t('admin.accountClassification.ownerByEmployeeId') }}</label></p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassification.employeeIdAttribute') }}</span>
              <select v-model="settings.employeeIdAttribute" :disabled="!settings.ownerByEmployeeId">
                <option value="employeeID">{{ 'employeeID' }}</option>
                <option value="employeeNumber">{{ 'employeeNumber' }}</option>
              </select>
            </label>
          </p>
          <p class="hint">{{ t('admin.accountClassification.settingsHint') }}</p>
          <button type="submit" class="btn-primary" :disabled="busy">{{ t('admin.accountClassification.saveSettings') }}</button>
          <button type="button" :disabled="busy" @click="reclassify">{{ t('admin.accountClassification.reclassifyNow') }}</button>
        </fieldset>
      </form>

      <section v-for="section in SECTIONS" :key="section.type">
        <h3>{{ t(section.headingKey) }}</h3>
        <p class="hint">{{ t(section.introKey) }}</p>
        <p>
          <button type="button" class="btn-primary" @click="router.push({ name: 'admin-account-classification-rule-create', query: { type: section.type } })">
            {{ t(section.newKey) }}
          </button>
        </p>
        <p v-if="rulesOf(section.type).length === 0">{{ t('admin.accountClassification.noRules') }}</p>
        <table v-else>
          <thead>
            <tr>
              <th>{{ t('admin.accountClassification.colOrder') }}</th>
              <th>{{ t('admin.accountClassification.colCondition') }}</th>
              <th>{{ t('admin.accountClassification.colClass') }}</th>
              <th>{{ t('common.enabled') }}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(rule, index) in rulesOf(section.type)" :key="rule.accountClassificationRuleKey">
              <td>{{ index + 1 }}</td>
              <td>
                <router-link :to="{ name: 'admin-account-classification-rule-edit', params: { ruleKey: rule.accountClassificationRuleKey } }">{{ condition(rule) }}</router-link>
              </td>
              <td>{{ t(CLASS_KEYS[rule.accountClass]) }}</td>
              <td>{{ rule.isEnabled ? t('admin.accountClassification.yes') : t('admin.accountClassification.no') }}</td>
              <td>
                <button type="button" :disabled="busy || index === 0" :aria-label="t('admin.accountClassification.moveUpLabel', { rule: condition(rule) })" @click="move(rule, 'up')">{{ t('admin.accountClassification.moveUp') }}</button>
                <button type="button" :disabled="busy || index === rulesOf(section.type).length - 1" :aria-label="t('admin.accountClassification.moveDownLabel', { rule: condition(rule) })" @click="move(rule, 'down')">{{ t('admin.accountClassification.moveDown') }}</button>
                <button type="button" :disabled="busy" :aria-label="t('admin.accountClassification.deleteLabel', { rule: condition(rule) })" @click="remove(rule)">{{ t('common.delete') }}</button>
              </td>
            </tr>
          </tbody>
        </table>
      </section>
    </template>
  </div>
</template>

<style scoped>
.intro,
.hint {
  max-width: 60em;
}
fieldset {
  max-width: 50em;
}
</style>
