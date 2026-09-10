<script setup>
// D-128: one shared instance, mounted once in App.vue -- see
// composables/useConfirmDialog.js for why this is a singleton rather than
// a per-page component. role="dialog"/aria-modal plus a small Tab-cycle
// focus trap between the two buttons (nothing else is focusable inside
// this dialog, so a full focus-trap library would be overkill) --
// Escape and the backdrop both cancel, matching common dialog conventions.
import { ref, watch, nextTick } from 'vue'
import { useConfirmDialogState, respondToConfirmDialog } from '../composables/useConfirmDialog'

const state = useConfirmDialogState()
const cancelButton = ref(null)
const confirmButton = ref(null)

watch(() => state.visible, async (visible) => {
  if (visible) {
    await nextTick()
    cancelButton.value?.focus()
  }
})

function onKeydown(event) {
  if (event.key === 'Escape') {
    respondToConfirmDialog(false)
    return
  }
  if (event.key !== 'Tab') return
  // Only two focusable elements in this dialog -- cycle between them
  // rather than letting focus escape to the page underneath.
  event.preventDefault()
  if (document.activeElement === cancelButton.value) {
    confirmButton.value?.focus()
  } else {
    cancelButton.value?.focus()
  }
}
</script>

<template>
  <div v-if="state.visible" class="confirm-dialog-backdrop" @click.self="respondToConfirmDialog(false)">
    <div
      class="confirm-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="confirm-dialog-message"
      @keydown="onKeydown"
    >
      <p id="confirm-dialog-message">{{ state.message }}</p>
      <p class="confirm-dialog-actions">
        <button type="button" ref="cancelButton" @click="respondToConfirmDialog(false)">Cancel</button>
        <button type="button" ref="confirmButton" class="btn-primary" @click="respondToConfirmDialog(true)">{{ state.confirmLabel }}</button>
      </p>
    </div>
  </div>
</template>

<style scoped>
.confirm-dialog-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}
.confirm-dialog {
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  border-radius: 0.25rem;
  padding: var(--space-4);
  max-width: 28rem;
  width: calc(100% - var(--space-4) * 2);
  box-shadow: 0 0.25rem 1rem rgba(0, 0, 0, 0.3);
}
.confirm-dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
}
</style>
