<script setup>
// D-300: Admin > Safe Access Policies -- the Safe roles (named sets of Safe permissions) and the policies the nightly
// load checks every Safe with accounts against: the first enabled policy, in order, whose Safe name pattern matches a
// Safe applies to it. Check now runs the check at once. ManageSafeAccessPolicies; GET/DELETE
// /api/admin/safe-access/roles and /policies, POST /api/admin/safe-access/check. Add and edit are on
// SafeRoleEdit.vue and SafeAccessPolicyEdit.vue; the results are on Reports > Safe Access.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { flash } from '../../composables/useFlash'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import { describeSafeAccessRule } from './safeAccessLabels'

const { t } = useI18n()
const router = useRouter()

const roles = ref([])
const policies = ref([])
const error = ref(null)
const loading = ref(true)
const checking = ref(false)

async function load() {
  loading.value = true
  try {
    const [rolesResponse, policiesResponse] = await Promise.all([fetch('/api/admin/safe-access/roles'), fetch('/api/admin/safe-access/policies')])
    for (const r of [rolesResponse, policiesResponse]) {
      if (!r.ok) throw new Error(t('common.requestFailed', { status: r.status }))
    }
    roles.value = await rolesResponse.json()
    policies.value = await policiesResponse.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(url, name) {
  if (!(await confirmDelete(t('admin.safeAccessPolicies.confirmDelete', { name })))) return
  error.value = null
  const response = await fetch(url, { method: 'DELETE' })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    error.value = problem?.detail ?? t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}

async function checkNow() {
  error.value = null
  checking.value = true
  try {
    const response = await fetch('/api/admin/safe-access/check', { method: 'POST' })
    if (!response.ok) {
      error.value = t('common.requestFailed', { status: response.status })
      return
    }
    flash(t('admin.safeAccessPolicies.checked'))
  } finally {
    checking.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ t('admin.safeAccessPolicies.title') }}</h2>
    <p class="hint">{{ t('admin.safeAccessPolicies.intro') }}</p>
    <p><router-link :to="{ name: 'reports-safe-access' }">{{ t('admin.safeAccessPolicies.reportLink') }}</router-link></p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <h3>{{ t('admin.safeAccessPolicies.policiesHeading') }}</h3>
      <p>
        <button type="button" class="btn-primary" @click="router.push({ name: 'admin-safe-access-policy-create' })">{{ t('admin.safeAccessPolicies.newPolicy') }}</button>
        <button type="button" :disabled="checking" @click="checkNow">{{ t('admin.safeAccessPolicies.checkNow') }}</button>
      </p>
      <table v-if="policies.length" class="policies">
        <thead>
          <tr>
            <th>{{ t('admin.safeAccessPolicies.colOrder') }}</th>
            <th>{{ t('common.name') }}</th>
            <th>{{ t('admin.safeAccessPolicies.colSafes') }}</th>
            <th>{{ t('admin.safeAccessPolicies.colRules') }}</th>
            <th>{{ t('admin.safeAccessPolicies.colEnabled') }}</th>
            <th><span class="visually-hidden">{{ t('admin.safeAccessPolicies.colActions') }}</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in policies" :key="p.safeAccessPolicyKey">
            <td>{{ p.sortOrder }}</td>
            <td><router-link :to="{ name: 'admin-safe-access-policy-edit', params: { policyKey: p.safeAccessPolicyKey } }">{{ p.policyName }}</router-link></td>
            <td><code>{{ p.safeNamePattern }}</code></td>
            <td>
              <ul class="rules">
                <li v-for="(r, i) in p.rules" :key="i">{{ describeSafeAccessRule(t, r) }}</li>
              </ul>
            </td>
            <td>{{ p.isEnabled ? t('common.yes') : t('common.no') }}</td>
            <td class="row-actions">
              <button type="button" @click="router.push({ name: 'admin-safe-access-policy-create', query: { copyOf: p.safeAccessPolicyKey } })">{{ t('admin.safeAccessPolicies.duplicate') }}</button>
              <button type="button" @click="remove(`/api/admin/safe-access/policies/${p.safeAccessPolicyKey}`, p.policyName)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="policies.length === 0" :message="t('admin.safeAccessPolicies.noPolicies')" />

      <h3>{{ t('admin.safeAccessPolicies.rolesHeading') }}</h3>
      <p class="hint">{{ t('admin.safeAccessPolicies.rolesIntro') }}</p>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-safe-role-create' })">{{ t('admin.safeAccessPolicies.newRole') }}</button></p>
      <table v-if="roles.length" class="roles">
        <thead>
          <tr>
            <th>{{ t('common.name') }}</th>
            <th>{{ t('admin.safeAccessPolicies.colPermissions') }}</th>
            <th>{{ t('admin.safeAccessPolicies.colUsedBy') }}</th>
            <th><span class="visually-hidden">{{ t('admin.safeAccessPolicies.colActions') }}</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in roles" :key="r.safeRoleKey">
            <td><router-link :to="{ name: 'admin-safe-role-edit', params: { roleKey: r.safeRoleKey } }">{{ r.roleName }}</router-link></td>
            <td>{{ r.permissions.join(', ') }}<template v-if="r.exactMatch">{{ ' ' + t('admin.safeAccessPolicies.exactly') }}</template></td>
            <td>{{ t('admin.safeAccessPolicies.ruleCount', { count: r.ruleCount }, r.ruleCount) }}</td>
            <td class="row-actions">
              <button type="button" @click="router.push({ name: 'admin-safe-role-create', query: { copyOf: r.safeRoleKey } })">{{ t('admin.safeAccessPolicies.duplicate') }}</button>
              <button type="button" @click="remove(`/api/admin/safe-access/roles/${r.safeRoleKey}`, r.roleName)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="roles.length === 0" :message="t('admin.safeAccessPolicies.noRoles')" />
    </template>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.rules {
  margin: 0;
  padding-left: 1.1rem;
}
</style>
