<script setup>
// D-124 Phase 4: Add/Edit moved off Targets.vue's own inline
// form/editing/startCreate/startEdit/cancelEdit state onto this routed
// page, mirroring RiskExceptionEdit.vue's create-vs-edit-by-route-param
// shape (isEditMode from the route param; loading/saving/error refs; save()
// POSTs or PUTs then navigates away). Targets.vue's nested Identifiers
// collection (add/remove rows, replace-all-on-save) moves here unchanged.
//
// No GET-by-id endpoint existed for Targets before this phase -- the old
// inline form worked off the already-loaded list row, never a fresh fetch.
// Since this page can be reached by a direct navigation or a page refresh
// (not just a click from the list page), and Targets is now paginated
// (D-124 Phase 3 -- 2,380 real rows), the list endpoint alone can't be
// trusted to include any one specific row. A new
// GET /api/admin/targets/{targetKey} (TargetsController.GetByKey /
// TargetRepository.GetByKeyAsync) was added alongside this page, mirroring
// RiskExceptionsController.GetByKey's existing shape -- Create/Update/
// Delete are otherwise completely unchanged.
//
// D-229: a linked target shows the target it's linked to; a primary lists
// its linked targets. Unlink (POST /api/admin/targets/{key}/unlink, asked
// first) is offered on both; a pair is never linked again automatically.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../composables/useConfirmDialog'
import { formatShortDate } from '../utils/formatLocale'

const { t } = useI18n()

const props = defineProps({ targetKey: { type: [String, Number], required: false, default: null } })
const router = useRouter()
const isEditMode = computed(() => props.targetKey !== null && props.targetKey !== undefined)

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)

const applications = ref([])
const identifierTypes = ref([])
const targetTypes = ref([])

// D-129: Discovery Source defaults to 'Manual' for a brand-new row created
// via this Add form (confirmed directly) -- edit mode overwrites this
// whole object with the real fetched row below, so this default only ever
// takes effect on create.
const editing = ref({ targetTypeKey: null, targetName: '', riskScore: 0, description: '', discoverySource: 'Manual', identifiers: [] })
const links = ref({ linkedToTargetKey: null, linkedToTargetName: null, linkedTargets: [] })
const unlinkError = ref(null)

function addIdentifierRow() {
  editing.value.identifiers.push({ identifierType: identifierTypes.value[0]?.identifierType ?? '', identifierValue: '' })
}
function removeIdentifierRow(index) {
  editing.value.identifiers.splice(index, 1)
}

onMounted(async () => {
  const referenceDataPromise = Promise.all([
    fetch('/api/applications').then(r => (r.ok ? r.json() : [])).then(data => { applications.value = data }).catch(() => {
      // Non-fatal -- the Application dropdown just won't have options if this fails.
    }),
    fetch('/api/admin/targets/identifier-types').then(r => (r.ok ? r.json() : [])).then(data => { identifierTypes.value = data }).catch(() => {
      // Non-fatal -- the Identifier Type dropdown just won't have options if this fails.
    }),
    fetch('/api/admin/targets/target-types').then(r => (r.ok ? r.json() : [])).then(data => { targetTypes.value = data }).catch(() => {
      // Non-fatal -- the Type dropdown just won't have options if this fails.
    })
  ])

  if (isEditMode.value) {
    try {
      await loadTarget()
    } catch (err) {
      loadError.value = err.message
    }
  }

  await referenceDataPromise
  if (!isEditMode.value) {
    editing.value.targetTypeKey = targetTypes.value[0]?.targetTypeKey ?? null
  }
  loading.value = false
})

async function fetchTarget(key) {
  const response = await fetch(`/api/admin/targets/${key}`)
  if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
  return response.json()
}

async function loadTarget() {
  const target = await fetchTarget(props.targetKey)
  const { linkedToTargetKey, linkedToTargetName, linkedTargets, ...fields } = target
  editing.value = { ...fields, identifiers: target.identifiers.map(i => ({ ...i })) }
  links.value = { linkedToTargetKey, linkedToTargetName, linkedTargets: linkedTargets ?? [] }
}

async function unlink(linkedKey, linkedName, primaryName) {
  unlinkError.value = null
  const confirmed = await confirmDelete({
    title: t('pages.targetEdit.unlinkTitle'),
    details: [t('pages.targetEdit.unlinkDetail', { name: linkedName, primary: primaryName })],
    warning: t('pages.targetEdit.unlinkWarning')
  }, t('pages.targetEdit.unlink'))
  if (!confirmed) return
  const response = await fetch(`/api/admin/targets/${linkedKey}/unlink`, { method: 'POST' })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    unlinkError.value = problem?.detail ?? t('pages.targetEdit.unlinkFailed', { status: response.status })
    return
  }
  const target = await fetchTarget(props.targetKey)
  links.value = { linkedToTargetKey: target.linkedToTargetKey, linkedToTargetName: target.linkedToTargetName, linkedTargets: target.linkedTargets ?? [] }
}

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const url = isEditMode.value ? `/api/admin/targets/${props.targetKey}` : '/api/admin/targets'
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
    router.push({ name: 'targets' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'targets' })
}
</script>

<template>
  <div>
    <h1>{{ isEditMode ? t('pages.targetEdit.titleEdit') : t('pages.targetEdit.titleNew') }}</h1>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p><label class="field-label"><span class="field-label-text">{{ t('pages.targetEdit.name') }}</span> <input v-model="editing.targetName" required /></label></p>
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('pages.targetEdit.type') }}</span>
          <select v-model="editing.targetTypeKey">
            <option v-for="type in targetTypes" :key="type.targetTypeKey" :value="type.targetTypeKey">{{ type.displayName }}</option>
          </select>
        </label>
      </p>
      <p><label class="field-label"><span class="field-label-text">{{ t('pages.targetEdit.riskScore') }}</span> <input v-model.number="editing.riskScore" type="number" min="0" max="1000" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('pages.targetEdit.description') }}</span> <input v-model="editing.description" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('pages.targetEdit.discoverySource') }}</span> <input v-model="editing.discoverySource" :placeholder="t('pages.targetEdit.discoverySourcePlaceholder')" /></label></p>

      <h3>{{ t('pages.targetEdit.identifiers') }}</h3>
      <div v-for="(identifier, index) in editing.identifiers" :key="index" class="filter-row">
        <label class="field-label">
          <span class="field-label-text">{{ t('pages.targetEdit.type') }}</span>
          <select v-model="identifier.identifierType">
            <option v-for="type in identifierTypes" :key="type.identifierType" :value="type.identifierType">{{ type.identifierType }}</option>
          </select>
        </label>
        <label class="field-label"><span class="field-label-text">{{ t('pages.targetEdit.value') }}</span> <input v-model="identifier.identifierValue" required /></label>
        <button type="button" @click="removeIdentifierRow(index)">{{ t('common.remove') }}</button>
      </div>
      <p><button type="button" @click="addIdentifierRow">{{ t('pages.targetEdit.addIdentifier') }}</button></p>

      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>

    <template v-if="isEditMode && !loading && !loadError">
      <p v-if="unlinkError" role="alert">{{ unlinkError }}</p>
      <section v-if="links.linkedToTargetKey">
        <h3>{{ t('pages.targetEdit.linkedToHeading') }}</h3>
        <p>
          {{ t('pages.targetEdit.linkedToIntro') }}
          <router-link :to="{ name: 'target-edit', params: { targetKey: links.linkedToTargetKey } }">{{ links.linkedToTargetName }}</router-link>
        </p>
        <p><button type="button" @click="unlink(props.targetKey, editing.targetName, links.linkedToTargetName)">{{ t('pages.targetEdit.unlink') }}</button></p>
      </section>
      <section v-if="links.linkedTargets.length">
        <h3>{{ t('pages.targetEdit.linkedTargetsHeading') }}</h3>
        <p>{{ t('pages.targetEdit.linkedTargetsIntro') }}</p>
        <table>
          <thead>
            <tr><th>{{ t('pages.targetEdit.colName') }}</th><th>{{ t('pages.targetEdit.colType') }}</th><th>{{ t('pages.targetEdit.colSource') }}</th><th>{{ t('pages.targetEdit.colLinked') }}</th><th></th></tr>
          </thead>
          <tbody>
            <tr v-for="linked in links.linkedTargets" :key="linked.targetKey">
              <td><router-link :to="{ name: 'target-edit', params: { targetKey: linked.targetKey } }">{{ linked.targetName }}</router-link></td>
              <td>{{ linked.targetTypeDisplayName }}</td>
              <td>{{ linked.discoverySource }}</td>
              <td>{{ formatShortDate(linked.linkedDate) }}</td>
              <td>
                <button type="button" :aria-label="t('pages.targetEdit.unlinkRow', { name: linked.targetName })"
                        @click="unlink(linked.targetKey, linked.targetName, editing.targetName)">{{ t('pages.targetEdit.unlink') }}</button>
              </td>
            </tr>
          </tbody>
        </table>
      </section>
    </template>
  </div>
</template>
