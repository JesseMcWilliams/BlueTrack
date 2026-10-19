<script setup>
// UI review theme 3: what a list says when it has no rows, so an empty table isn't mistaken for a failed load. The
// caller decides the wording (none yet, or none match the filters) and whether to show it (not while loading).
defineProps({
  show: { type: Boolean, required: true },
  message: { type: String, required: true }
})
</script>

<template>
  <p v-if="show" class="empty-list">{{ message }}</p>
</template>

<style scoped>
.empty-list {
  margin: var(--space-3) 0;
  color: var(--color-text-muted, inherit);
}
</style>
