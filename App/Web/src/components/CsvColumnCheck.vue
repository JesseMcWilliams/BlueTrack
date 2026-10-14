<script setup>
// D-225: on a Bulk Actions upload, reads the chosen file's header row in
// the browser (only its first chunk; nothing is uploaded until Import) and
// shows which of the feed's fields it supplies under their own names --
// what an upload with no mapping profile reads. When some fields would
// only match by a synonym or are missing, "Create a mapping profile from
// these columns" opens a new profile pre-filled from the same headers.
import { ref, computed, watch } from 'vue'
import { useRouter } from 'vue-router'
import { readCsvHeaders, matchHeaders, fieldsFor } from '../utils/csvHeaderMapping'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({
  file: { type: Object, default: null },
  feedType: { type: String, required: true },
  // A profile is chosen: its mapping applies, so this check isn't shown.
  profileChosen: { type: Boolean, default: false }
})
const router = useRouter()

const headers = ref([])
const readError = ref(null)

watch(() => props.file, async file => {
  headers.value = []
  readError.value = null
  if (!file) return
  try {
    headers.value = (await readCsvHeaders(file)).headers
  } catch (err) {
    readError.value = t('shell.csvColumnCheck.readError', { error: err.message })
  }
}, { immediate: true })

const fields = computed(() => fieldsFor(props.feedType))
const result = computed(() => matchHeaders(headers.value, fields.value))
const exact = computed(() => result.value.matches.filter(m => m.how === 'exact'))
const bySynonym = computed(() => result.value.matches.filter(m => m.how === 'synonym'))
const missingRequired = computed(() =>
  fields.value.filter(f => f.required && !exact.value.some(m => m.field === f.name)).map(f => f.name))

function createProfile() {
  router.push({
    name: 'admin-import-mapping-profile-create',
    state: { sampleHeaders: [...headers.value], sampleFeedType: props.feedType }
  })
}
</script>

<template>
  <div v-if="file && !profileChosen" class="csv-column-check" role="status">
    <p v-if="readError">{{ readError }}</p>
    <template v-else-if="headers.length > 0">
      <p>
        {{ t('shell.csvColumnCheck.columns', { count: headers.length }) }}
        <template v-if="exact.length">{{ t('shell.csvColumnCheck.readByName', { fields: exact.map(m => m.field).join(', ') }) }}</template>
        <template v-if="missingRequired.length"> <strong>{{ t('shell.csvColumnCheck.missingLabel') }}</strong> {{ t('shell.csvColumnCheck.missingList', { fields: missingRequired.join(', ') }) }}</template>
      </p>
      <p v-if="bySynonym.length || missingRequired.length">
        <template v-if="bySynonym.length">{{ t('shell.csvColumnCheck.couldBeMapped', { mappings: bySynonym.map(m => t('shell.csvColumnCheck.mapping', { header: m.header, field: m.field })).join(', ') }) + ' ' }}</template>
        <button type="button" @click="createProfile">{{ t('shell.csvColumnCheck.createProfile') }}</button>
      </p>
    </template>
  </div>
</template>
