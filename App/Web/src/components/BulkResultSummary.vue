<script setup>
// D-190: what a bulk action did -- "N <verb>, M skipped", and why each was skipped.
defineProps({
  /** { changed, skipped: [{ key, name, reason }], verb } */
  result: { type: Object, required: true }
})
</script>

<template>
  <div role="status">
    <p>{{ result.changed }} {{ result.verb }}, {{ result.skipped.length }} skipped<template v-if="result.skipped.length"> (still selected)</template>.</p>
    <ul v-if="result.skipped.length">
      <li v-for="s in result.skipped" :key="s.key">{{ s.name ?? `#${s.key}` }}: {{ s.reason }}</li>
    </ul>
  </div>
</template>
