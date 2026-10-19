<script setup>
// UI review theme 3: the one-time "saved" line after a save that leaves the page. Mounted once in App.vue; see
// composables/useFlash.js for when a message shows and when it goes.
import { useI18n } from 'vue-i18n'
import { useFlashState, flashDismiss } from '../composables/useFlash'

const { t } = useI18n()
const state = useFlashState()
</script>

<template>
  <p v-if="state.message" class="flash-message" role="status">
    {{ state.message }}
    <button type="button" class="flash-dismiss" :aria-label="t('common.dismiss')" @click="flashDismiss()">&times;</button>
  </p>
</template>

<style scoped>
.flash-message {
  margin: 0;
  padding: var(--space-2) var(--space-4);
  border-bottom: 1px solid var(--color-border);
  background: var(--color-row-hover, rgba(0, 128, 0, 0.08));
}
.flash-dismiss {
  margin-left: var(--space-3);
  border: 0;
  background: transparent;
  cursor: pointer;
  font-size: 1.1em;
  line-height: 1;
}
</style>
