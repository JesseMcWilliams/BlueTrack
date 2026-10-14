<script setup>
// D-213: the user menu's About -- BlueTrack's version, from GET /api/about.
// Laid out like ConfirmDialog.vue (role="dialog", aria-modal, a backdrop):
// Close is the only control, so it takes focus and keeps it (Tab stays put);
// Escape and the backdrop also close it.
import { ref, onMounted, nextTick } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const emit = defineEmits(['close'])
const version = ref(null)
const error = ref(null)
const closeButton = ref(null)

onMounted(async () => {
  await nextTick()
  closeButton.value?.focus()
  try {
    const response = await fetch('/api/about')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    version.value = (await response.json()).version
  } catch (err) {
    error.value = err.message
  }
})

function onKeydown(event) {
  if (event.key === 'Escape') emit('close')
  else if (event.key === 'Tab') event.preventDefault()
}
</script>

<template>
  <div class="about-dialog-backdrop" @click.self="emit('close')">
    <div class="about-dialog" role="dialog" aria-modal="true" aria-labelledby="about-dialog-title" @keydown="onKeydown">
      <h2 id="about-dialog-title">{{ t('shell.about.title') }}</h2>
      <p v-if="error" role="alert">{{ t('shell.about.loadFailed', { error }) }}</p>
      <p v-else-if="version === null" role="status">{{ t('common.loading') }}</p>
      <p v-else>{{ t('shell.about.version', { version }) }}</p>
      <p class="about-dialog-actions">
        <button ref="closeButton" type="button" class="btn-primary" @click="emit('close')">{{ t('common.close') }}</button>
      </p>
    </div>
  </div>
</template>

<style scoped>
.about-dialog-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}
.about-dialog {
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  border-radius: 0.25rem;
  padding: var(--space-4);
  max-width: 28rem;
  width: calc(100% - var(--space-4) * 2);
  box-shadow: 0 0.25rem 1rem rgba(0, 0, 0, 0.3);
}
.about-dialog h2 {
  margin-top: 0;
}
.about-dialog-actions {
  display: flex;
  justify-content: flex-end;
}
</style>
