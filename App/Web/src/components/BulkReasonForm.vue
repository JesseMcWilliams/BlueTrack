<script setup>
// D-185 / D-190: the reason a bulk delete or revoke needs, as a small inline
// form (the shared confirm dialog has no text box). Emits submit(reason).
import { ref } from 'vue'

defineProps({
  /** e.g. "delete 3 targets" -- shown as "Reason to delete 3 targets:" */
  action: { type: String, required: true },
  buttonLabel: { type: String, required: true },
  saving: { type: Boolean, default: false }
})
const emit = defineEmits(['submit', 'cancel'])
const reason = ref('')
</script>

<template>
  <form class="bulk-panel" @submit.prevent="emit('submit', reason)">
    <label class="field-label">
      <span class="field-label-text">Reason to {{ action }}:</span>
      <input v-model="reason" size="60" maxlength="1000" required />
    </label>
    <button type="submit" class="btn-primary" :disabled="saving || !reason.trim()">{{ buttonLabel }}</button>
    <button type="button" @click="emit('cancel')">Cancel</button>
  </form>
</template>

<style scoped>
.bulk-panel {
  margin: 0.5rem 0 1rem;
}
</style>
