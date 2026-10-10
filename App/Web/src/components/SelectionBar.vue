<script setup>
// D-182 / D-190: the bulk-action selection bar shared by the list pages --
// a checkbox that turns selection on, the selected count, Select all on
// page / Select all matching / Invert selection on page / Clear, and a slot
// for the page's own action buttons. "Select all matching" asks the
// list's keys endpoint (the same filters as the list), which returns no
// keys when more match than the bulk limit.
import { ref, computed } from 'vue'

const props = defineProps({
  selection: { type: Object, required: true },
  pageKeys: { type: Array, required: true },
  filteredCount: { type: Number, default: null },
  /** URL of the list's keys endpoint, with the current filters as its query. */
  keysUrl: { type: String, required: true },
  toggleLabel: { type: String, default: 'Select for bulk actions' }
})

const message = ref(null)
const selectedOnPage = computed(() => props.pageKeys.filter(k => props.selection.isSelected(k)).length)

async function selectAllMatching() {
  message.value = null
  const response = await fetch(props.keysUrl)
  if (!response.ok) {
    message.value = `Could not select: ${response.status}`
    return
  }
  const result = await response.json()
  const max = result.maxItems ?? result.maxAccounts
  if (result.matchingCount > max) {
    message.value = `${result.matchingCount} match, more than the bulk limit of ${max}. Narrow the filters first.`
    return
  }
  props.selection.add(result.keys ?? result.accountKeys)
}
</script>

<template>
  <div class="selection-bar">
    <label><input type="checkbox" :checked="selection.enabled" @change="selection.setEnabled($event.target.checked)" /> {{ toggleLabel }}</label>
    <template v-if="selection.enabled">
      <strong role="status">{{ selection.count }} selected</strong><span v-if="selection.count"> ({{ selectedOnPage }} on this page)</span>
      <button type="button" @click="selection.add(pageKeys)">Select all on page</button>
      <button type="button" @click="selectAllMatching">Select all matching ({{ filteredCount ?? 0 }})</button>
      <button type="button" @click="selection.invert(pageKeys)">Invert selection on page</button>
      <button type="button" :disabled="selection.count === 0" @click="selection.clear()">Clear</button>
      <slot />
    </template>
  </div>
  <p v-if="message" role="alert">{{ message }}</p>
</template>

<style scoped>
.selection-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem 1rem;
  margin: 0.5rem 0;
}
</style>
