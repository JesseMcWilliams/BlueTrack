<script setup>
// UI review W12-1: the notice shown shortly before the idle sign-out (useIdleSignOut). Activity of any kind clears it,
// so the button is for the keyboard-only and screen-reader user who has not moved the pointer.
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
defineProps({ warning: { type: Object, default: null } })
defineEmits(['stay'])
</script>

<template>
  <p v-if="warning" class="idle-warning" role="alert">
    {{ t('shell.idleWarning.message', { count: warning.minutesLeft }, warning.minutesLeft) }}
    <button type="button" @click="$emit('stay')">{{ t('shell.idleWarning.stay') }}</button>
  </p>
</template>

<style scoped>
.idle-warning {
  margin: 0;
  padding: var(--space-2) var(--space-4);
  border-bottom: 1px solid var(--color-border);
}
</style>
