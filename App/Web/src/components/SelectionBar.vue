<script setup>
// D-182 / D-190: the bulk-action selection bar shared by the list pages --
// a checkbox that turns selection on, the selected count, Select all on
// page / Select all matching / Invert selection on page / Clear, and a slot
// for the page's own action buttons. "Select all matching" asks the
// list's keys endpoint (the same filters as the list), which returns no
// keys when more match than the bulk limit.
import { ref, computed } from 'vue'
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps({
  selection: { type: Object, required: true },
  pageKeys: { type: Array, required: true },
  filteredCount: { type: Number, default: null },
  /** URL of the list's keys endpoint, with the current filters as its query. */
  keysUrl: { type: String, required: true },
  // null = shell.selectionBar.toggle ("Select for bulk actions").
  toggleLabel: { type: String, default: null }
})

const message = ref(null)
const selectedOnPage = computed(() => props.pageKeys.filter(k => props.selection.isSelected(k)).length)

async function selectAllMatching() {
  message.value = null
  const response = await fetch(props.keysUrl)
  if (!response.ok) {
    message.value = t('shell.selectionBar.couldNotSelect', { status: response.status })
    return
  }
  const result = await response.json()
  const max = result.maxItems ?? result.maxAccounts
  if (result.matchingCount > max) {
    message.value = t('shell.selectionBar.tooMany', { matching: formatCount(result.matchingCount), max: formatCount(max) })
    return
  }
  props.selection.add(result.keys ?? result.accountKeys)
}
</script>

<template>
  <div class="selection-bar">
    <label><input type="checkbox" :checked="selection.enabled" @change="selection.setEnabled($event.target.checked)" /> {{ toggleLabel ?? t('shell.selectionBar.toggle') }}</label>
    <template v-if="selection.enabled">
      <strong role="status">{{ t('shell.selectionBar.selected', { count: formatCount(selection.count) }) }}</strong><span v-if="selection.count">{{ ' ' + t('shell.selectionBar.onThisPage', { count: formatCount(selectedOnPage) }) }}</span>
      <button type="button" @click="selection.add(pageKeys)">{{ t('shell.selectionBar.selectAllOnPage') }}</button>
      <button type="button" @click="selectAllMatching">{{ t('shell.selectionBar.selectAllMatching', { count: formatCount(filteredCount ?? 0) }) }}</button>
      <button type="button" @click="selection.invert(pageKeys)">{{ t('shell.selectionBar.invert') }}</button>
      <button type="button" :disabled="selection.count === 0" @click="selection.clear()">{{ t('common.clear') }}</button>
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
