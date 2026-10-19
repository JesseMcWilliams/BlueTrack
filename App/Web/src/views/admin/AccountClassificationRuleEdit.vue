<script setup>
// D-233: add/edit one account classification rule (D-124: its own page).
// The rule type comes from ?type= on New; the attribute list is the
// server's allow-list (GET /api/admin/account-classification), and the
// server validates again (400 with the reason). Saving reclassifies.
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { flash } from '../../composables/useFlash'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({ ruleKey: { type: [String, Number], required: false, default: null } })
const route = useRoute()
const router = useRouter()
const isEditMode = computed(() => props.ruleKey !== null && props.ruleKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const allowedAttributes = ref([])
const nameAttributes = ref(['sAMAccountName', 'userPrincipalName'])

const TYPES = ['Attribute', 'Name', 'OU']
const OPERATORS = {
  Attribute: ['Equals', 'Contains', 'StartsWith', 'IsSet'],
  Name: ['StartsWith', 'EndsWith', 'Pattern'],
  OU: ['Contains', 'EndsWith']
}
const TYPE_KEYS = {
  Attribute: 'admin.accountClassificationRuleEdit.typeAttribute',
  Name: 'admin.accountClassificationRuleEdit.typeName',
  OU: 'admin.accountClassificationRuleEdit.typeOu'
}
const OPERATOR_KEYS = {
  Equals: 'admin.accountClassificationRuleEdit.opEquals',
  Contains: 'admin.accountClassificationRuleEdit.opContains',
  StartsWith: 'admin.accountClassificationRuleEdit.opStartsWith',
  EndsWith: 'admin.accountClassificationRuleEdit.opEndsWith',
  IsSet: 'admin.accountClassificationRuleEdit.opIsSet',
  Pattern: 'admin.accountClassificationRuleEdit.opPattern'
}
const CLASS_KEYS = {
  Primary: 'admin.accountClassificationRuleEdit.classPrimary',
  Secondary: 'admin.accountClassificationRuleEdit.classSecondary',
  Admin: 'admin.accountClassificationRuleEdit.classAdmin'
}
const HINT_KEYS = {
  Attribute: 'admin.accountClassificationRuleEdit.hintAttribute',
  Name: 'admin.accountClassificationRuleEdit.hintName',
  OU: 'admin.accountClassificationRuleEdit.hintOu'
}

const initialType = TYPES.includes(route?.query?.type) ? route.query.type : 'Attribute'
const editing = ref({ ruleType: initialType, attributeName: initialType === 'Attribute' ? 'adminCount' : initialType === 'Name' ? 'sAMAccountName' : null, operator: OPERATORS[initialType][0], value: '', accountClass: 'Admin', isEnabled: true })

const operators = computed(() => OPERATORS[editing.value.ruleType] ?? [])

function onTypeChange() {
  const type = editing.value.ruleType
  editing.value.attributeName = type === 'Attribute' ? (allowedAttributes.value[0] ?? 'adminCount') : type === 'Name' ? 'sAMAccountName' : null
  if (!OPERATORS[type].includes(editing.value.operator)) editing.value.operator = OPERATORS[type][0]
}

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/account-classification')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    const body = await response.json()
    allowedAttributes.value = body.allowedAttributes ?? []
    if (body.nameAttributes) nameAttributes.value = body.nameAttributes
    if (isEditMode.value) {
      const match = body.rules.find(r => String(r.accountClassificationRuleKey) === String(props.ruleKey))
      if (!match) throw new Error(t('admin.accountClassificationRuleEdit.notFound'))
      editing.value = { ...match, value: match.value ?? '' }
    }
  } catch (err) {
    loadError.value = err.message
  }
  loading.value = false
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/account-classification/rules/${props.ruleKey}` : '/api/admin/account-classification/rules'
    const response = await fetch(url, {
      method: isEditMode.value ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(editing.value)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    flash(t('common.saved'))
    router.push({ name: 'admin-account-classification' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-account-classification' })
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? t('admin.accountClassificationRuleEdit.editTitle') : t('admin.accountClassificationRuleEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassificationRuleEdit.ruleType') }}</span>
          <select v-model="editing.ruleType" @change="onTypeChange">
            <option v-for="type in TYPES" :key="type" :value="type">{{ t(TYPE_KEYS[type]) }}</option>
          </select>
        </label>
      </p>
      <p class="hint">{{ t(HINT_KEYS[editing.ruleType]) }}</p>
      <p v-if="editing.ruleType === 'Attribute'">
        <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassificationRuleEdit.attribute') }}</span>
          <select v-model="editing.attributeName" required>
            <option v-for="name in allowedAttributes" :key="name" :value="name">{{ name }}</option>
          </select>
        </label>
      </p>
      <p v-if="editing.ruleType === 'Name'">
        <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassificationRuleEdit.nameAttribute') }}</span>
          <select v-model="editing.attributeName">
            <option v-for="name in nameAttributes" :key="name" :value="name">{{ name }}</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassificationRuleEdit.operator') }}</span>
          <select v-model="editing.operator">
            <option v-for="op in operators" :key="op" :value="op">{{ t(OPERATOR_KEYS[op]) }}</option>
          </select>
        </label>
      </p>
      <p v-if="editing.operator !== 'IsSet'">
        <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassificationRuleEdit.value') }}</span>
          <input v-model="editing.value" required maxlength="500" />
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.accountClassificationRuleEdit.accountClass') }}</span>
          <select v-model="editing.accountClass">
            <option v-for="c in ['Admin', 'Secondary', 'Primary']" :key="c" :value="c">{{ t(CLASS_KEYS[c]) }}</option>
          </select>
        </label>
      </p>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('common.enabled') }}</label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
</style>
