<script setup>
// D-190: what a bulk action did -- "N <verb>, M skipped", and why each was skipped.
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

defineProps({
  /** { changed, skipped: [{ key, name, reason }], verb } */
  result: { type: Object, required: true }
})
</script>

<template>
  <div role="status">
    <p>{{ t(result.skipped.length ? 'shell.bulkResult.summaryStillSelected' : 'shell.bulkResult.summary', { changed: formatCount(result.changed), verb: result.verb, skipped: formatCount(result.skipped.length) }) }}</p>
    <ul v-if="result.skipped.length">
      <li v-for="s in result.skipped" :key="s.key">{{ t('shell.bulkResult.skippedItem', { name: s.name ?? t('shell.bulkResult.unnamedItem', { key: s.key }), reason: s.reason }) }}</li>
    </ul>
  </div>
</template>
