<script setup>
// Calls GET /api/reports/unresolved-entitlement-members (ReportsController,
// ViewDashboard), backed by dbo.vw_unresolved_entitlement_members: Safe
// entitlements whose member ID from the Entitlements export matched no known
// user or group (D-145/D-147). D-232: the member's name is shown; the load
// resolves a member by exact name when one user or group has it, and holders
// of ConfirmReconciliation can Resolve a member here (link it to a user or
// group, or label it External / cloud role) or Undo that. Resolved members
// are left out unless Show resolved is ticked. Resolve acts on the member, so
// every safe row with that member ID changes together.
import { ref, computed, onMounted, nextTick } from 'vue'
import { formatDate } from '../../utils/formatDate'
import { useRightsStore } from '../../stores/rights'
import { confirmDelete } from '../../composables/useConfirmDialog'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import Combobox from '../../components/Combobox.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const rights = useRightsStore()
const canResolve = computed(() => rights.hasPermission('ConfirmReconciliation'))

const base = '/api/reports/unresolved-entitlement-members'
const items = ref([])
const error = ref(null)
const actionError = ref(null)
// UI review W5-5: what a save did, shown beside the form's old place (the top of the page).
const notice = ref(null)
const loading = ref(true)
const showResolved = ref(false)
const busy = ref(false)

// The member being resolved: { memberType, sourceMemberId, memberName, count }.
const editing = ref(null)
const choice = ref('Linked')
const linkTo = ref(null)
const note = ref('')
const formError = ref(null)

async function load() {
  loading.value = true
  error.value = null
  try {
    const response = await fetch(`${base}?showResolved=${showResolved.value ? 'true' : 'false'}`)
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

function resolutionText(item) {
  switch (item.resolutionMethod) {
    case 'ById': return t('reports.unresolvedEntitlementMembers.byId', { name: item.resolvedToName })
    case 'ByName': return t('reports.unresolvedEntitlementMembers.byName', { name: item.resolvedToName })
    case 'Manual': return t('reports.unresolvedEntitlementMembers.linkedBy', { user: item.resolvedByName, name: item.resolvedToName })
    case 'External': return item.resolutionNote
      ? t('reports.unresolvedEntitlementMembers.externalWithNote', { user: item.resolvedByName, note: item.resolutionNote })
      : t('reports.unresolvedEntitlementMembers.external', { user: item.resolvedByName })
    default: return item.resolutionMethod
  }
}

async function problemMessage(response) {
  const problem = await response.json().catch(() => null)
  return problem?.detail ?? problem?.title ?? t('common.requestFailed', { status: response.status })
}

async function searchPrincipals(text) {
  const params = new URLSearchParams({ memberType: editing.value.memberType, q: text })
  const response = await fetch(`${base}/principals?${params.toString()}`)
  if (!response.ok) throw new Error(t('reports.unresolvedEntitlementMembers.searchFailed', { status: response.status }))
  return response.json()
}

const principalLabel = principal => principal.name

async function startResolve(item) {
  actionError.value = null
  formError.value = null
  notice.value = null
  editing.value = {
    memberType: item.memberType,
    sourceMemberId: item.unresolvedMemberId,
    memberName: item.memberName ?? item.unresolvedMemberId,
    count: items.value.filter(i => i.memberType === item.memberType && i.unresolvedMemberId === item.unresolvedMemberId).length
  }
  choice.value = item.resolutionMethod === 'External' ? 'External' : 'Linked'
  note.value = item.resolutionMethod === 'External' ? (item.resolutionNote ?? '') : ''
  const key = item.memberType === 'User' ? item.userKey : item.groupKey
  linkTo.value = key ? { key, name: item.resolvedToName } : null
  // Pre-fill the name match: the one user or group with exactly this name.
  if (!linkTo.value && item.memberName && item.memberName.trim().length >= 2) {
    try {
      const wanted = item.memberName.trim().toLowerCase()
      const matches = (await searchPrincipals(item.memberName.trim())).filter(p => p.name.toLowerCase() === wanted)
      if (matches.length === 1 && editing.value?.sourceMemberId === item.unresolvedMemberId) linkTo.value = matches[0]
    } catch {
      // No suggestion; the search box still works.
    }
  }
}

// UI review W5-5: the form opens at the top of the page, away from the row's button, so bring it into view and put
// focus in it (its heading), instead of leaving the user looking at the row.
async function showResolveForm(item) {
  const opening = startResolve(item)
  await nextTick()
  const form = document.getElementById('resolve-heading')
  form?.scrollIntoView?.({ block: 'start' })
  form?.focus()
  await opening
}

function cancel() {
  editing.value = null
  formError.value = null
}

async function save() {
  formError.value = null
  if (choice.value === 'Linked' && !linkTo.value) {
    formError.value = editing.value.memberType === 'User'
      ? t('reports.unresolvedEntitlementMembers.chooseUser')
      : t('reports.unresolvedEntitlementMembers.chooseGroup')
    return
  }
  const body = {
    memberType: editing.value.memberType,
    sourceMemberId: editing.value.sourceMemberId,
    resolution: choice.value,
    userKey: choice.value === 'Linked' && editing.value.memberType === 'User' ? linkTo.value.key : null,
    groupKey: choice.value === 'Linked' && editing.value.memberType === 'Group' ? linkTo.value.key : null,
    note: choice.value === 'External' ? note.value : null
  }
  busy.value = true
  try {
    const response = await fetch(`${base}/resolve`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) throw new Error(await problemMessage(response))
    notice.value = t('reports.unresolvedEntitlementMembers.resolvedNotice', { name: editing.value.memberName })
    editing.value = null
    await load()
  } catch (err) {
    formError.value = err.message
  } finally {
    busy.value = false
  }
}

async function undo(item) {
  if (!(await confirmDelete(
    t('reports.unresolvedEntitlementMembers.undoConfirm', { name: item.memberName ?? item.unresolvedMemberId }),
    t('reports.unresolvedEntitlementMembers.undo')
  ))) return
  actionError.value = null
  busy.value = true
  try {
    const response = await fetch(`${base}/unresolve`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ memberType: item.memberType, sourceMemberId: item.unresolvedMemberId })
    })
    if (!response.ok) throw new Error(await problemMessage(response))
    await load()
  } catch (err) {
    actionError.value = err.message
  } finally {
    busy.value = false
  }
}

function onShowResolvedChange() {
  editing.value = null
  load()
}

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('reports.unresolvedEntitlementMembers.title') }}</h2>
    <p class="hint">{{ t('reports.unresolvedEntitlementMembers.hint') }}</p>
    <p v-if="canResolve" class="hint">{{ t('reports.unresolvedEntitlementMembers.hintResolve') }}</p>
    <ReportExportButtons :path="`${base}/export`" :params="{ showResolved }" />
    <p><label><input v-model="showResolved" type="checkbox" @change="onShowResolvedChange" /> {{ t('reports.unresolvedEntitlementMembers.showResolved') }}</label></p>

    <section v-if="editing" class="resolve-form" aria-labelledby="resolve-heading">
      <h3 id="resolve-heading" tabindex="-1">{{ t('reports.unresolvedEntitlementMembers.resolveTitle', { name: editing.memberName }) }}</h3>
      <p>{{ t('reports.unresolvedEntitlementMembers.resolveAppliesTo', { id: editing.sourceMemberId, count: editing.count }, editing.count) }}</p>
      <fieldset>
        <legend>{{ t('reports.unresolvedEntitlementMembers.resolveAs') }}</legend>
        <p>
          <label>
            <input v-model="choice" type="radio" name="resolution" value="Linked" />
            {{ editing.memberType === 'User' ? t('reports.unresolvedEntitlementMembers.linkUser') : t('reports.unresolvedEntitlementMembers.linkGroup') }}
          </label>
        </p>
        <div v-if="choice === 'Linked'" class="indent">
          <Combobox
            v-model="linkTo"
            :label="editing.memberType === 'User' ? t('reports.unresolvedEntitlementMembers.userSearchLabel') : t('reports.unresolvedEntitlementMembers.groupSearchLabel')"
            :search="searchPrincipals"
            :item-key="principal => principal.key"
            :item-label="principalLabel"
            :placeholder="t('reports.unresolvedEntitlementMembers.searchPlaceholder')"
          >
            <template #option="{ item: principal }">
              <strong>{{ principal.name }}</strong>
              <template v-if="principal.detail"><br /><small>{{ principal.detail }}</small></template>
            </template>
          </Combobox>
        </div>
        <p>
          <label>
            <input v-model="choice" type="radio" name="resolution" value="External" />
            {{ t('reports.unresolvedEntitlementMembers.externalChoice') }}
          </label>
        </p>
        <div v-if="choice === 'External'" class="indent">
          <label class="field-label">
            <span>{{ t('reports.unresolvedEntitlementMembers.noteLabel') }}</span>
            <textarea v-model="note" rows="2" maxlength="500"></textarea>
          </label>
        </div>
      </fieldset>
      <p v-if="formError" role="alert">{{ formError }}</p>
      <button type="button" :disabled="busy" @click="save">{{ t('common.save') }}</button>
      <button type="button" :disabled="busy" @click="cancel">{{ t('common.cancel') }}</button>
    </section>

    <p v-if="actionError" role="alert">{{ actionError }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.unresolvedEntitlementMembers.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ showResolved ? t('reports.unresolvedEntitlementMembers.emptyAll') : t('reports.unresolvedEntitlementMembers.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.unresolvedEntitlementMembers.colSafe') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colSource') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colMemberType') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colName') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colMemberId') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colResolution') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colExpiration') }}</th>
          <th>{{ t('reports.unresolvedEntitlementMembers.colSnapshotDate') }}</th>
          <th v-if="canResolve"><span class="visually-hidden">{{ t('reports.unresolvedEntitlementMembers.colActions') }}</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.entitlementKey">
          <td>{{ item.safeName }}</td>
          <td>{{ item.sourceSystemName }}</td>
          <td>{{ item.memberType }}</td>
          <td>{{ item.memberName }}</td>
          <td>{{ item.unresolvedMemberId }}</td>
          <td>
            <span v-if="!item.resolutionMethod" class="unresolved-flag">{{ t('reports.unresolvedEntitlementMembers.unresolved') }}</span>
            <template v-else>{{ resolutionText(item) }}</template>
          </td>
          <td>{{ formatDate(item.membershipExpirationDate) }}</td>
          <td>{{ formatDate(item.snapshotDate) }}</td>
          <td v-if="canResolve">
            <!-- D-277: Self-Hosted members can't be resolved by hand yet (Planning_Backlog.md). -->
            <small v-if="item.sourceSystemCode === 'SELFHOSTED'">{{ t('reports.unresolvedEntitlementMembers.selfHostedNoResolve') }}</small>
            <button v-else type="button" :disabled="busy"
                    :aria-label="t('reports.unresolvedEntitlementMembers.resolveLabel', { name: item.memberName ?? item.unresolvedMemberId, safe: item.safeName })"
                    @click="showResolveForm(item)">{{ t('reports.unresolvedEntitlementMembers.resolve') }}</button>
            <button v-if="item.resolutionMethod === 'Manual' || item.resolutionMethod === 'External'" type="button" :disabled="busy"
                    :aria-label="t('reports.unresolvedEntitlementMembers.undoLabel', { name: item.memberName ?? item.unresolvedMemberId, safe: item.safeName })"
                    @click="undo(item)">{{ t('reports.unresolvedEntitlementMembers.undo') }}</button>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}

.unresolved-flag {
  font-weight: bold;
  color: var(--color-error-text);
}

.resolve-form {
  max-width: 40em;
  margin: 1rem 0;
  padding: 0.75rem 1rem;
  border: 1px solid var(--color-border);
}

.indent {
  margin-left: 1.75rem;
  margin-bottom: 0.5rem;
}

.field-label {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}
</style>
