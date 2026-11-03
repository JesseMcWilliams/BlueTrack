<script setup>
// D-300: add or edit a Safe access policy: the Safes it applies to (a name pattern; * is any text), its place in the
// order (the first enabled policy whose pattern matches a Safe applies), and its rules: Required (a member whose name
// matches, {SafeName} being the Safe's own name, holds a Safe role, optionally with people in it) or Forbidden (no
// member matching, or none holding the role named). POST/PUT /api/admin/safe-access/policies; the roles from
// GET /api/admin/safe-access/roles.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { flash, savedMessage } from '../../composables/useFlash'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const props = defineProps({ policyKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.policyKey !== null && props.policyKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const roles = ref([])
const editing = ref({ policyName: '', safeNamePattern: '*', sortOrder: 1, isEnabled: true, description: '', rules: [] })

function addRule(ruleType) {
  editing.value.rules.push({ ruleType, memberPattern: '', safeRoleKey: ruleType === 'Required' ? roles.value[0]?.safeRoleKey ?? null : null, requirePeople: false })
}

function removeRule(index) {
  editing.value.rules.splice(index, 1)
}

onMounted(async () => {
  try {
    const [rolesResponse, policiesResponse] = await Promise.all([fetch('/api/admin/safe-access/roles'), fetch('/api/admin/safe-access/policies')])
    for (const r of [rolesResponse, policiesResponse]) {
      if (!r.ok) throw new Error(t('common.requestFailed', { status: r.status }))
    }
    roles.value = await rolesResponse.json()
    const policies = await policiesResponse.json()
    if (isEditMode.value) {
      const match = policies.find(p => String(p.safeAccessPolicyKey) === String(props.policyKey))
      if (!match) throw new Error(t('admin.safeAccessPolicyEdit.notFound'))
      editing.value = { ...match, description: match.description ?? '', rules: match.rules.map(r => ({ ...r })) }
    } else {
      editing.value.sortOrder = policies.reduce((max, p) => Math.max(max, p.sortOrder), 0) + 1
      addRule('Required')
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
    const url = isEditMode.value ? `/api/admin/safe-access/policies/${props.policyKey}` : '/api/admin/safe-access/policies'
    const body = {
      policyName: editing.value.policyName,
      safeNamePattern: editing.value.safeNamePattern,
      sortOrder: editing.value.sortOrder,
      isEnabled: editing.value.isEnabled,
      description: editing.value.description,
      rules: editing.value.rules.map(r => ({ ruleType: r.ruleType, memberPattern: r.memberPattern, safeRoleKey: r.safeRoleKey || null, requirePeople: r.ruleType === 'Required' && r.requirePeople }))
    }
    const response = await fetch(url, { method: isEditMode.value ? 'PUT' : 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    flash(savedMessage(t, editing.value.policyName))
    router.push({ name: 'admin-safe-access-policies' })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ isEditMode ? t('admin.safeAccessPolicyEdit.editTitle') : t('admin.safeAccessPolicyEdit.newTitle') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>
    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.safeAccessPolicyEdit.name') }}</span> <input v-model="editing.policyName" required maxlength="200" /></label></p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.safeAccessPolicyEdit.safes') }}</span> <input v-model="editing.safeNamePattern" required maxlength="300" /></label>
        <small>{{ t('admin.safeAccessPolicyEdit.safesHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.safeAccessPolicyEdit.order') }}</span> <input v-model.number="editing.sortOrder" type="number" required /></label>
        <small>{{ t('admin.safeAccessPolicyEdit.orderHint') }}</small>
      </p>
      <p><label><input v-model="editing.isEnabled" type="checkbox" /> {{ t('admin.safeAccessPolicyEdit.enabled') }}</label></p>

      <fieldset>
        <legend>{{ t('admin.safeAccessPolicyEdit.rules') }}</legend>
        <p class="hint">{{ t('admin.safeAccessPolicyEdit.rulesHint') }}</p>
        <div v-for="(r, i) in editing.rules" :key="i" class="rule">
          <strong>{{ r.ruleType === 'Required' ? t('admin.safeAccessPolicyEdit.required') : t('admin.safeAccessPolicyEdit.forbidden') }}</strong>
          <label>
            <span class="visually-hidden">{{ t('admin.safeAccessPolicyEdit.memberPattern', { n: i + 1 }) }}</span>
            <input v-model="r.memberPattern" required maxlength="300" :placeholder="r.ruleType === 'Required' ? '{SafeName}_Users' : 'Vault Admins'" />
          </label>
          <label>
            <span class="visually-hidden">{{ t('admin.safeAccessPolicyEdit.role', { n: i + 1 }) }}</span>
            <select v-model="r.safeRoleKey" :required="r.ruleType === 'Required'">
              <option v-if="r.ruleType === 'Forbidden'" :value="null">{{ t('admin.safeAccessPolicyEdit.anyAccess') }}</option>
              <option v-for="role in roles" :key="role.safeRoleKey" :value="role.safeRoleKey">{{ role.roleName }}</option>
            </select>
          </label>
          <label v-if="r.ruleType === 'Required'"><input v-model="r.requirePeople" type="checkbox" /> {{ t('admin.safeAccessPolicyEdit.requirePeople') }}</label>
          <button type="button" :aria-label="t('admin.safeAccessPolicyEdit.removeRule', { n: i + 1 })" @click="removeRule(i)">{{ t('common.remove') }}</button>
        </div>
        <p v-if="roles.length === 0" class="hint">{{ t('admin.safeAccessPolicyEdit.noRoles') }}</p>
        <p>
          <button type="button" :disabled="roles.length === 0" @click="addRule('Required')">{{ t('admin.safeAccessPolicyEdit.addRequired') }}</button>
          <button type="button" @click="addRule('Forbidden')">{{ t('admin.safeAccessPolicyEdit.addForbidden') }}</button>
        </p>
      </fieldset>

      <p><label class="field-label"><span class="field-label-text">{{ t('admin.safeAccessPolicyEdit.description') }}</span> <textarea v-model="editing.description" rows="2" maxlength="1000"></textarea></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="router.push({ name: 'admin-safe-access-policies' })">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.rule {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  align-items: center;
  margin-bottom: 0.5rem;
}
</style>
